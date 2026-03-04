# SNMP Monitoring System — Architecture Design

## Table of Contents
1. [Overview](#overview)
2. [The Complete Stack](#the-complete-stack)
3. [OID Map](#oid-map)
4. [MediatR — The Core](#mediatr)
5. [Metrics Design](#metrics-design)
6. [Pipeline Design](#pipeline-design)
7. [OpenTelemetry Pipeline](#opentelemetry-pipeline)
8. [Grafana Visualization](#grafana-visualization)
9. [Complete Flow](#complete-flow)
10. [Program.cs](#programcs)
11. [Docker Compose](#docker-compose)

---

## Overview

Every OID received — from a trap or a poll — is looked up in a single shared OID map. If found, it gets a human name. If not, it is labelled `Unknown`. The value is recorded as a gauge and pushed to Prometheus via OpenTelemetry.

**Stack:**
- **C# .NET 9** — runtime
- **MediatR** — routes SNMP events to the metric handler
- **OpenTelemetry SDK** — metrics push
- **OTel Collector** — receives OTLP, pushes via `remote_write`
- **Prometheus** — time-series storage
- **Grafana** — dashboards and alerts

> **Full push pipeline:** App → OTLP → Collector → `remote_write` → Prometheus. Nothing pulls.

---

## The Complete Stack

```
SNMP Device
    ↓
UDP:162 (trap) / Poll GET
    ↓
C# .NET 9 Background Service
    ↓
MediatR → OtelMetricHandler
    ↓
OTel SDK  →  OTLP gRPC push  →  OTel Collector (:4317)
                                       ↓  remote_write
                                 Prometheus (:9090)
                                       ↓
                                 Grafana
```

---

## OID Map

One map, shared by traps and polls. Maps OID string to human name.

```csharp
public class OidMap
{
    private readonly Dictionary<string, string> _map;

    public OidMap(IConfiguration config)
    {
        _map = LoadFromFile(config["OidMap:FilePath"]);
    }

    public string Resolve(string oid)
    {
        return _map.TryGetValue(oid, out var name) ? name : "Unknown";
    }
}
```

### oid-map.json

```json
{
  "1.3.6.1.2.1.1.3.0":       "sysUpTime",
  "1.3.6.1.2.1.2.2.1.10":    "ifInOctets",
  "1.3.6.1.2.1.2.2.1.16":    "ifOutOctets",
  "1.3.6.1.2.1.2.2.1.14":    "ifInErrors",
  "1.3.6.1.2.1.25.3.3.1.2":  "hrProcessorLoad",
  "1.3.6.1.2.1.25.2.3.1.6":  "hrStorageUsed",
  "1.3.6.1.6.3.1.1.5.3":     "linkDown",
  "1.3.6.1.6.3.1.1.5.4":     "linkUp",
  "1.3.6.1.6.3.1.1.5.1":     "coldStart",
  "1.3.6.1.6.3.1.1.5.2":     "warmStart"
}
```

OID in map → `human_name = "linkDown"`. OID not in map → `human_name = "Unknown"`. Same logic for traps and polls.

---

## MediatR — The Core

The listener publishes an event. MediatR routes it to `OtelMetricHandler`. The listener knows nothing about metrics.

```csharp
// Message — carries OID data + resolved human name
public record SnmpOidReceived : INotification
{
    public string   OID          { get; init; }
    public string   AgentAddress { get; init; }
    public string   Value        { get; init; }
    public string   Source       { get; init; }  // "trap" or "poll"
    public string   HumanName    { get; init; }  // set by OidResolutionBehavior
}

// Handler — records the gauge, nothing else
public class OtelMetricHandler : INotificationHandler<SnmpOidReceived>
{
    private readonly SnmpMetric _metric;

    public Task Handle(SnmpOidReceived e, CancellationToken ct)
    {
        _metric.Record(e.OID, e.HumanName, e.AgentAddress, e.Value, e.Source);
        return Task.CompletedTask;
    }
}
```

MediatR is the extension point. When a second handler is needed (database, alerting, etc.) it is added here without touching the listener or the metric handler.

---

## Metrics Design

One gauge. Every OID sets it with its latest value.

```csharp
public class SnmpMetric
{
    private static readonly Meter Meter = new("Snmp.Oid");

    private static readonly ObservableGauge<double> OidGauge =
        Meter.CreateObservableGauge<double>(
            name:          "snmp.oid",
            unit:          "{value}",
            description:   "Current OID value",
            observeValues: () => _cache.ObserveAll());

    private static readonly OidGaugeCache _cache = new();

    public void Record(string oid, string humanName,
                       string agent, string value, string source)
    {
        double numeric = double.TryParse(value, out var n) ? n : 1;
        _cache.Set(oid, agent, source, numeric, humanName, value);
    }
}
```

### OidGaugeCache

```csharp
public class OidGaugeCache
{
    private readonly ConcurrentDictionary<string, GaugeEntry> _state = new();

    public void Set(string oid, string agent, string source,
                    double value, string humanName, string rawValue)
    {
        _state[$"{oid}:{agent}:{source}"] =
            new GaugeEntry(oid, agent, source, value, humanName, rawValue);
    }

    public IEnumerable<Measurement<double>> ObserveAll()
    {
        foreach (var e in _state.Values)
            yield return new Measurement<double>(
                e.Value,
                new KeyValuePair<string, object?>("oid",        e.OID),
                new KeyValuePair<string, object?>("human_name", e.HumanName),
                new KeyValuePair<string, object?>("agent",      e.Agent),
                new KeyValuePair<string, object?>("source",     e.Source),
                new KeyValuePair<string, object?>("raw_value",  e.RawValue));
    }
}
```

### Prometheus Output

```prometheus
snmp_oid{oid="1.3.6.1.2.1.25.3.3.1.2", human_name="hrProcessorLoad",
  agent="192.168.1.1", source="poll", raw_value="73"} 73

snmp_oid{oid="1.3.6.1.6.3.1.1.5.3", human_name="linkDown",
  agent="192.168.1.1", source="trap", raw_value="1"} 1

snmp_oid{oid="1.3.6.1.4.1.9999.1.2.3", human_name="Unknown",
  agent="172.16.0.5", source="trap", raw_value="42"} 42
```

---

## Pipeline Design

Behaviors run before the handler on every event. Each owns one concern.

```
OID arrives (trap or poll)
      ↓
① LoggingBehavior        — log OID + agent
      ↓
② ExceptionBehavior      — catch all failures, never crash
      ↓
③ ValidationBehavior     — reject malformed OID or IP
      ↓
④ OidResolutionBehavior  — OidMap.Resolve() → sets HumanName
      ↓
OtelMetricHandler        — record gauge
```

### OidResolutionBehavior

```csharp
public class OidResolutionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly OidMap _oidMap;

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is SnmpOidReceived e)
            request = (TRequest)(object)(e with { HumanName = _oidMap.Resolve(e.OID) });

        return await next();
    }
}
```

| # | Behavior | Responsibility |
|---|----------|----------------|
| ① | Logging | Log every OID received |
| ② | Exception | Catch all errors, return default |
| ③ | Validation | Reject invalid OID / IP format |
| ④ | OidResolution | Resolve human name from map |

---

## OpenTelemetry Pipeline

```
App  →  OTLP gRPC (:4317)  →  OTel Collector  →  remote_write  →  Prometheus
```

### OTel SDK Registration

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Snmp.Oid")
            .AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = new Uri("http://otel-collector:4317");
                otlp.Protocol = OtlpExportProtocol.Grpc;
            });
    });
```

### OTel Collector Config

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317

processors:
  batch:
    timeout: 5s

exporters:
  prometheusremotewrite:
    endpoint: http://prometheus:9090/api/v1/write
    add_metric_suffixes: true
    resource_to_telemetry_conversion:
      enabled: true

service:
  pipelines:
    metrics:
      receivers:  [otlp]
      processors: [batch]
      exporters:  [prometheusremotewrite]
```

### Prometheus Config

```bash
# remote_write receiver must be enabled
prometheus --web.enable-remote-write-receiver
```

---

## Grafana Visualization

```promql
# All traps — live table
snmp_oid{source="trap"}

# All polls — time series
snmp_oid{source="poll"}

# Unknown OIDs — alert on this
snmp_oid{human_name="Unknown"}

# Specific OID over time
snmp_oid{human_name="hrProcessorLoad"}
```

### Alert

```yaml
- alert: UnknownOidReceived
  expr:  snmp_oid{human_name="Unknown"} > 0
  for:   0m
  annotations:
    summary: "Unknown OID {{ $labels.oid }} from {{ $labels.agent }}"
```

### Dashboard

```
┌──────────────────────────────────────────────────┐
│  STAT: Unknown OIDs 🔴         STAT: Devices      │
└──────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────┐
│  TABLE — Live traps  (snmp_oid{source="trap"})   │
│  OID                  human_name   raw_value      │
│  1.3.6.1.6.3.1.1.5.3 linkDown     1              │
│  1.3.6.1.4.1.9999... Unknown 🔴   42             │
└──────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────┐
│  TIME SERIES — Poll values  (snmp_oid{source="poll"}) │
└──────────────────────────────────────────────────┘
```

---

## Complete Flow

```
Trap / Poll arrives
      ↓
Background Service
      ↓
IMediator.Publish(new SnmpOidReceived { OID, Agent, Value, Source })
      ↓
① LoggingBehavior      — "OID=1.3.6.1.6.3.1.1.5.3 Agent=192.168.1.1"
② ExceptionBehavior    — try/catch wrapper
③ ValidationBehavior   — valid OID and IP?  ✗ → discard
④ OidResolutionBehavior— OidMap.Resolve() → HumanName = "linkDown" | "Unknown"
      ↓
OtelMetricHandler
      ↓
SnmpMetric.Record() → OidGaugeCache.Set()
      ↓
OTel SDK export (every 5s)
      ↓
OTLP push → Collector → remote_write → Prometheus → Grafana
```

---

## Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Snmp.Oid")
            .AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = new Uri(
                    builder.Configuration["OtelCollector:Endpoint"]
                    ?? "http://otel-collector:4317");
                otlp.Protocol = OtlpExportProtocol.Grpc;
            });
    });

// MediatR
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// Pipeline — outermost to innermost
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ExceptionBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(OidResolutionBehavior<,>));

// SNMP
builder.Services.AddSingleton<OidMap>();
builder.Services.AddSingleton<SnmpMetric>();
builder.Services.AddSingleton<OidGaugeCache>();

// Background services
builder.Services.AddHostedService<SnmpTrapListener>();
builder.Services.AddHostedService<SnmpPoller>();

var app = builder.Build();
app.Run();
```

---

## Docker Compose

```yaml
services:

  snmp-monitor:
    build: .
    environment:
      - OtelCollector__Endpoint=http://otel-collector:4317
    depends_on:
      - otel-collector

  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    volumes:
      - ./otel-collector-config.yaml:/etc/otel/config.yaml
    command: ["--config=/etc/otel/config.yaml"]
    ports:
      - "4317:4317"

  prometheus:
    image: prom/prometheus:latest
    command:
      - '--config.file=/etc/prometheus/prometheus.yml'
      - '--web.enable-remote-write-receiver'
    ports:
      - "9090:9090"

  grafana:
    image: grafana/grafana:latest
    ports:
      - "3000:3000"
    depends_on:
      - prometheus
```

---

*C# .NET 9 · MediatR · OpenTelemetry · OTel Collector · Prometheus · Grafana*
