﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Threading;
using System.Text;
using Newtonsoft.Json;
using WebSocket.Uniswap.Models;
using RedDuck.Candleswap.Candles.CSharp;
using Microsoft.Extensions.Logging;
using static RedDuck.Candleswap.Candles.Types;

namespace WebSocket.Uniswap.Infrastructure
{
    internal class WebSocketConnection
    {
        private System.Net.WebSockets.WebSocket _webSocket;
        private int _receivePayloadBufferSize;

        public Guid Id { get; } = Guid.NewGuid();

        public WebSocketCloseStatus? CloseStatus { get; private set; } = null;

        public string CloseStatusDescription { get; private set; } = null;

        public event EventHandler<string> ReceiveText;

        public event EventHandler<byte[]> ReceiveBinary;

        public static event EventHandler<string> ReceiveCandleUpdate;

        //private readonly List<(Pair, int)> _subscribedPairsWithResolutions = new();

        public WebSocketConnection(System.Net.WebSockets.WebSocket webSocket, int receivePayloadBufferSize)
        {
            _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
            _receivePayloadBufferSize = receivePayloadBufferSize;
        }

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            return _webSocket.SendAsync(buffer: new ArraySegment<byte>(array: Encoding.ASCII.GetBytes(message),
                                                                  offset: 0,
                                                                  count: message.Length),
                                                                  messageType: WebSocketMessageType.Text,
                                                                  endOfMessage: true,
                                                                  cancellationToken: cancellationToken);
        }

        public Task SendAsync(byte[] message, CancellationToken cancellationToken)
        {
            return _webSocket.SendAsync(buffer: message,
                                        messageType: WebSocketMessageType.Text,
                                        endOfMessage: true,
                                        cancellationToken: cancellationToken);
        }

        public async Task ReceiveMessagesUntilCloseAsync(ILogicService logic, ICandleStorageService candleStorage,
                                                         ILogger<WebSocketConnection> logger)
        {
            try
            {
                byte[] receivePayloadBuffer = new byte[_receivePayloadBufferSize];
                WebSocketReceiveResult webSocketReceiveResult = await _webSocket.ReceiveAsync(new ArraySegment<byte>(receivePayloadBuffer), CancellationToken.None);
                while (webSocketReceiveResult.MessageType != WebSocketMessageType.Close)
                {
                    if (webSocketReceiveResult.MessageType == WebSocketMessageType.Binary)
                    {
                        var webSocketMessage = Encoding.UTF8.GetString(receivePayloadBuffer).TrimEnd('\0').ToUpper();
                        if (webSocketMessage == "PING")
                        {
                            OnReceivePingPong(webSocketMessage);
                        }
                        else
                            OnReceiveBinary(receivePayloadBuffer);
                    }
                    else
                    {
                        var webSocketMessage = Encoding.UTF8.GetString(receivePayloadBuffer).TrimEnd('\0');
                        if (webSocketMessage == "PING")
                        {
                            OnReceivePingPong(webSocketMessage);
                        }
                        else if (webSocketMessage.Contains("candles"))
                        {
                            await OnReceiveCandlesRequest(logic, candleStorage, webSocketMessage);
                        }
                        else
                            OnReceiveText(webSocketMessage);
                    }

                    webSocketReceiveResult = await _webSocket.ReceiveAsync(new ArraySegment<byte>(receivePayloadBuffer), CancellationToken.None);
                }

                CloseStatus = webSocketReceiveResult.CloseStatus.Value;
                CloseStatusDescription = webSocketReceiveResult.CloseStatusDescription;
            }
            catch (WebSocketException wsex) when (wsex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {

            }
            catch(Exception ex)
            {
                logger.LogError(ex.ToString());
            }
        }

        private void OnReceiveText(string webSocketMessage)
        {
            ReceiveText?.Invoke(this, webSocketMessage);
        }

        private void OnReceiveBinary(byte[] webSocketMessage)
        {
            ReceiveBinary?.Invoke(this, webSocketMessage);
        }

        private void OnReceivePingPong(string _)
        {
            var webSocketMessage = "PONG";
            ReceiveText?.Invoke(this, webSocketMessage);
        }

        private void OnCandleUpdateReceived(string candle)
        {
            ReceiveCandleUpdate?.Invoke(this, candle);
        }

        private async Task OnReceiveCandlesRequest(ILogicService logic, ICandleStorageService candleStorage, string webSocketMessage)
        {
            string processedMessage;
            if (webSocketMessage.Count(symbol => symbol == '{' || symbol == '}') % 2 == 0)
                processedMessage = webSocketMessage;
            else
                processedMessage = webSocketMessage.Trim('}');

            var webSocketRequest = JsonConvert.DeserializeObject<CandleUpdate>(processedMessage);
            var arrayKeyParam = webSocketRequest.KeyParam.Split(':');
            //int resolution = GetResolution(arrayKeyParam[1]);
            if(arrayKeyParam.Length < 4)
            {
                await SendAsync("Params should be correct", CancellationToken.None);
            }
            if (string.IsNullOrEmpty(arrayKeyParam[2]) || string.IsNullOrEmpty(arrayKeyParam[3]))
            {
                await SendAsync("Two tokens should be provided", CancellationToken.None);
                return;
            }
            if (!int.TryParse(arrayKeyParam[1], out int period) || period == default)
            {
                await SendAsync("Period should be in seconds", CancellationToken.None);
                return;
            }

            switch (webSocketRequest.EventType)
            {
                case "subscribe":
                    await CandleEvent.SubscribeCandlesAsync(logic, candleStorage, arrayKeyParam[2], arrayKeyParam[3],
                                                            OnCandleUpdateReceived, period, Id);
                    break;
                case "unsubscribe":
                    await CandleEvent.UnsubscribeCandlesAsync(candleStorage, arrayKeyParam[2], arrayKeyParam[3], period, Id);
                    break;
            }

            ReceiveText?.Invoke(this, webSocketMessage);
        }

        private static int GetResolution(string arrayKeyParam)
        {
            return arrayKeyParam switch
            {
                "1h" => 3600,
                "30m" => 1800,
                "5m" => 300,
                "1m" => 60,
                "30s" => 30,
                "10s" => 10,
                _ => 10
            };
        }
    }
}
