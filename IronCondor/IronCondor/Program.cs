using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IBApi;

namespace IronCondor
{
    internal class Program
    {
        public class IronCondorTrader : EWrapper
        {
            private EClientSocket clientSocket;
            private readonly EReaderMonitorSignal signal = new EReaderMonitorSignal();
            private Dictionary<double, int> conIds = new Dictionary<double, int>();
            private double triggerPrice;
            private bool contractsReceived = false;
            private int timeoutInSeconds = 10;  // Timeout in seconds

            public IronCondorTrader()
            {
                clientSocket = new EClientSocket(this, signal);
            }

            public async Task StartAsync()
            {
                clientSocket.eConnect("127.0.0.1", 4001, 1); // Connect to IBKR
                await Task.Delay(1000);  // Allow connection time

                var reader = new EReader(clientSocket, signal);
                reader.Start();
                new Task(() =>
                {
                    while (clientSocket.IsConnected())
                    {
                        signal.waitForSignal();
                        reader.processMsgs();
                    }
                }).Start();

                Console.Write("Enter the symbol (e.g., AAPL): ");
                string symbol = Console.ReadLine();

                Console.Write("Enter expiry date (YYYYMMDD, e.g., 20250619): ");
                string expiryDate = Console.ReadLine();

                Console.Write("Enter the trigger underlying price (e.g., 100): ");
                triggerPrice = Convert.ToDouble(Console.ReadLine());

                await RequestConIdsAsync(symbol, expiryDate);

                bool timeoutOccurred = await WaitForContractsAsync(timeoutInSeconds);

                if (!timeoutOccurred && contractsReceived)
                {
                    Console.WriteLine("All 4 contract details received.");
                    await AskToPlaceOrderAsync();
                }
                else
                {
                    Console.WriteLine("Timeout reached without receiving all contract details.");
                }
            }

            private async Task RequestConIdsAsync(string symbol, string expiryDate)
            {
                double spread = 5;
                double[] strikes = { triggerPrice + spread, triggerPrice + (2 * spread), triggerPrice - spread, triggerPrice - (2 * spread) };

                foreach (double strike in strikes)
                {
                    string optionType = strike >= triggerPrice ? "C" : "P";
                    Contract optionContract = GetOptionContract(symbol, expiryDate, strike, optionType);
                    clientSocket.reqContractDetails((int)strike, optionContract);
                }
            }

            private Contract GetOptionContract(string symbol, string expiryDate, double strike, string optionType)
            {
                return new Contract
                {
                    Symbol = symbol,
                    SecType = "OPT",
                    Currency = "USD",
                    Exchange = "SMART",
                    LastTradeDateOrContractMonth = expiryDate,
                    Strike = strike,
                    Right = optionType,
                    Multiplier = "100"
                };
            }

            private async Task<bool> WaitForContractsAsync(int timeoutInSeconds)
            {
                int elapsedTime = 0;
                while (elapsedTime < timeoutInSeconds && conIds.Count < 4)
                {
                    await Task.Delay(1000);
                    elapsedTime++;
                }

                return elapsedTime >= timeoutInSeconds;
            }

            private async Task AskToPlaceOrderAsync()
            {
                Console.Write("Do you want to execute the Iron Condor order? (yes/no): ");
                string response = Console.ReadLine().ToLower();

                if (response == "yes")
                {
                    await PlaceIronCondorOrderAsync();
                }
                else
                {
                    Console.WriteLine("Order not executed.");
                }
            }


            private async Task PlaceIronCondorOrderAsync1()
            {
                ComboLeg leg1 = new ComboLeg { ConId = conIds[triggerPrice + 5], Ratio = 1, Action = "SELL", Exchange = "SMART" };
                ComboLeg leg2 = new ComboLeg { ConId = conIds[triggerPrice + 10], Ratio = 1, Action = "SELL", Exchange = "SMART" };
                ComboLeg leg3 = new ComboLeg { ConId = conIds[triggerPrice - 5], Ratio = 1, Action = "BUY", Exchange = "SMART" };
                ComboLeg leg4 = new ComboLeg { ConId = conIds[triggerPrice - 10], Ratio = 1, Action = "BUY", Exchange = "SMART" };

                Contract comboContract = new Contract
                {
                    Symbol = "IronCondor",
                    SecType = "BAG",
                    Currency = "USD",
                    Exchange = "SMART"
                };

                Order comboOrder = new Order
                {
                    Action = "SELL",
                    OrderType = "MKT",
                    TotalQuantity = 1,
                 };

                List<ComboLeg> comboLegs = new List<ComboLeg> { leg1, leg2, leg3, leg4 };
                clientSocket.placeOrder(1001, comboContract, comboOrder);
                Console.WriteLine("Iron Condor order placed!");
            }




            public static async Task Main()
            {
                IronCondorTrader trader = new IronCondorTrader();
                await trader.StartAsync();
            }

            public void contractDetails(int reqId, ContractDetails contractDetails)
            {
                conIds[reqId] = contractDetails.Contract.ConId;
                Console.WriteLine($"Received ConId: {contractDetails.Contract.ConId} for {contractDetails.Contract.Strike} {contractDetails.Contract.Right}");

                if (conIds.Count == 4)
                {
                    contractsReceived = true;
                }
            }

            public void contractDetailsEnd(int reqId)
            {
                throw new NotImplementedException();
            }

            public void error(Exception e) { Console.WriteLine(e.ToString()); }

            public void error(string str) { Console.WriteLine(str); }

            public void error(int id, int errorCode, string errorMsg) { Console.WriteLine($"Error {id}, {errorCode}: {errorMsg}"); }

            public void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
            {
                throw new NotImplementedException();
            }

            public void currentTime(long time)
            {
                throw new NotImplementedException();
            }

            public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
            {
                throw new NotImplementedException();
            }

            public void tickSize(int tickerId, int field, decimal size)
            {
                throw new NotImplementedException();
            }

            public void tickString(int tickerId, int field, string value)
            {
                throw new NotImplementedException();
            }

            public void tickGeneric(int tickerId, int field, double value)
            {
                throw new NotImplementedException();
            }

            public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate)
            {
                throw new NotImplementedException();
            }

            public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract)
            {
                throw new NotImplementedException();
            }

            public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice)
            {
                throw new NotImplementedException();
            }

            public void tickSnapshotEnd(int tickerId)
            {
                throw new NotImplementedException();
            }

            public void nextValidId(int orderId)
            {
                throw new NotImplementedException();
            }

            public void managedAccounts(string accountsList)
            {
                throw new NotImplementedException();
            }

            public void connectionClosed()
            {
                throw new NotImplementedException();
            }

            public void accountSummary(int reqId, string account, string tag, string value, string currency)
            {
                throw new NotImplementedException();
            }

            public void accountSummaryEnd(int reqId)
            {
                throw new NotImplementedException();
            }

            public void bondContractDetails(int reqId, ContractDetails contract)
            {
                throw new NotImplementedException();
            }

            public void updateAccountValue(string key, string value, string currency, string accountName)
            {
                throw new NotImplementedException();
            }

            public void updatePortfolio(Contract contract, decimal position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName)
            {
                throw new NotImplementedException();
            }

            public void updateAccountTime(string timestamp)
            {
                throw new NotImplementedException();
            }

            public void accountDownloadEnd(string account)
            {
                throw new NotImplementedException();
            }

            public void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
            {
                throw new NotImplementedException();
            }

            public void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
            {
                throw new NotImplementedException();
            }

            public void openOrderEnd()
            {
                throw new NotImplementedException();
            }

            public void execDetails(int reqId, Contract contract, Execution execution)
            {
                throw new NotImplementedException();
            }

            public void execDetailsEnd(int reqId)
            {
                throw new NotImplementedException();
            }

            public void commissionReport(CommissionReport commissionReport)
            {
                throw new NotImplementedException();
            }

            public void fundamentalData(int reqId, string data)
            {
                throw new NotImplementedException();
            }

            public void historicalData(int reqId, Bar bar)
            {
                throw new NotImplementedException();
            }

            public void historicalDataUpdate(int reqId, Bar bar)
            {
                throw new NotImplementedException();
            }

            public void historicalDataEnd(int reqId, string start, string end)
            {
                throw new NotImplementedException();
            }

            public void marketDataType(int reqId, int marketDataType)
            {
                throw new NotImplementedException();
            }

            public void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size)
            {
                throw new NotImplementedException();
            }

            public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth)
            {
                throw new NotImplementedException();
            }

            public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange)
            {
                throw new NotImplementedException();
            }

            public void position(string account, Contract contract, decimal pos, double avgCost)
            {
                throw new NotImplementedException();
            }

            public void positionEnd()
            {
                throw new NotImplementedException();
            }

            public void realtimeBar(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal WAP, int count)
            {
                throw new NotImplementedException();
            }

            public void scannerParameters(string xml)
            {
                throw new NotImplementedException();
            }

            public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
            {
                throw new NotImplementedException();
            }

            public void scannerDataEnd(int reqId)
            {
                throw new NotImplementedException();
            }

            public void receiveFA(int faDataType, string faXmlData)
            {
                throw new NotImplementedException();
            }

            public void verifyMessageAPI(string apiData)
            {
                throw new NotImplementedException();
            }

            public void verifyCompleted(bool isSuccessful, string errorText)
            {
                throw new NotImplementedException();
            }

            public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge)
            {
                throw new NotImplementedException();
            }

            public void verifyAndAuthCompleted(bool isSuccessful, string errorText)
            {
                throw new NotImplementedException();
            }

            public void displayGroupList(int reqId, string groups)
            {
                throw new NotImplementedException();
            }

            public void displayGroupUpdated(int reqId, string contractInfo)
            {
                throw new NotImplementedException();
            }

            public void connectAck()
            {
                throw new NotImplementedException();
            }

            public void positionMulti(int requestId, string account, string modelCode, Contract contract, decimal pos, double avgCost)
            {
                throw new NotImplementedException();
            }

            public void positionMultiEnd(int requestId)
            {
                throw new NotImplementedException();
            }

            public void accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency)
            {
                throw new NotImplementedException();
            }

            public void accountUpdateMultiEnd(int requestId)
            {
                throw new NotImplementedException();
            }

            public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes)
            {
                throw new NotImplementedException();
            }

            public void securityDefinitionOptionParameterEnd(int reqId)
            {
                throw new NotImplementedException();
            }

            public void softDollarTiers(int reqId, SoftDollarTier[] tiers)
            {
                throw new NotImplementedException();
            }

            public void familyCodes(FamilyCode[] familyCodes)
            {
                throw new NotImplementedException();
            }

            public void symbolSamples(int reqId, ContractDescription[] contractDescriptions)
            {
                throw new NotImplementedException();
            }

            public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions)
            {
                throw new NotImplementedException();
            }

            public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData)
            {
                throw new NotImplementedException();
            }

            public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap)
            {
                throw new NotImplementedException();
            }

            public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions)
            {
                throw new NotImplementedException();
            }

            public void newsProviders(NewsProvider[] newsProviders)
            {
                throw new NotImplementedException();
            }

            public void newsArticle(int requestId, int articleType, string articleText)
            {
                throw new NotImplementedException();
            }

            public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline)
            {
                throw new NotImplementedException();
            }

            public void historicalNewsEnd(int requestId, bool hasMore)
            {
                throw new NotImplementedException();
            }

            public void headTimestamp(int reqId, string headTimestamp)
            {
                throw new NotImplementedException();
            }

            public void histogramData(int reqId, HistogramEntry[] data)
            {
                throw new NotImplementedException();
            }

            public void rerouteMktDataReq(int reqId, int conId, string exchange)
            {
                throw new NotImplementedException();
            }

            public void rerouteMktDepthReq(int reqId, int conId, string exchange)
            {
                throw new NotImplementedException();
            }

            public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements)
            {
                throw new NotImplementedException();
            }

            public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL)
            {
                throw new NotImplementedException();
            }

            public void pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value)
            {
                throw new NotImplementedException();
            }

            public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done)
            {
                throw new NotImplementedException();
            }

            public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done)
            {
                throw new NotImplementedException();
            }

            public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done)
            {
                throw new NotImplementedException();
            }

            public void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, TickAttribLast tickAttribLast, string exchange, string specialConditions)
            {
                throw new NotImplementedException();
            }

            public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize, decimal askSize, TickAttribBidAsk tickAttribBidAsk)
            {
                throw new NotImplementedException();
            }

            public void tickByTickMidPoint(int reqId, long time, double midPoint)
            {
                throw new NotImplementedException();
            }

            public void orderBound(long orderId, int apiClientId, int apiOrderId)
            {
                throw new NotImplementedException();
            }

            public void completedOrder(Contract contract, Order order, OrderState orderState)
            {
                throw new NotImplementedException();
            }

            public void completedOrdersEnd()
            {
                throw new NotImplementedException();
            }

            public void replaceFAEnd(int reqId, string text)
            {
                throw new NotImplementedException();
            }

            public void wshMetaData(int reqId, string dataJson)
            {
                throw new NotImplementedException();
            }

            public void wshEventData(int reqId, string dataJson)
            {
                throw new NotImplementedException();
            }

            public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, HistoricalSession[] sessions)
            {
                throw new NotImplementedException();
            }

            public void userInfo(int reqId, string whiteBrandingId)
            {
                throw new NotImplementedException();
            }
        }
    }
}
