using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinanceBotProd
{
    // =========================================================================
    // 1. CAPA ENTITY: Modelos de Escaneo y Billetera Real
    // =========================================================================
    namespace Entity
    {
        public class BinanceAssetScanEntity
        {
            public string Symbol { get; set; } = string.Empty;
            public decimal BidPrice { get; set; }
            public decimal AskPrice { get; set; }
            public decimal VolatilityPct { get; set; }
            public decimal Rsi14 { get; set; }
            public decimal DynamicTargetPct { get; set; }
            public int FiltersPassed { get; set; }
            public bool IsSafeEntry { get; set; }
            public string StatusText { get; set; } = string.Empty;
        }

        public class DashboardStateEntity
        {
            public bool IsBotRunning { get; set; }
            public string ApiHealthStatus { get; set; } = string.Empty;
            
            public decimal RealBinanceUsdtBalance { get; set; }
            public bool IsApiConnected { get; set; }
            
            public decimal TotalActiveTradeCapital { get; set; }
            public decimal ProfitVaultUsd { get; set; }
            public int WinningTradesCount { get; set; }
            
            public bool HasActivePosition { get; set; }
            public string ActiveSymbol { get; set; } = string.Empty;
            public decimal ActiveEntryPrice { get; set; }
            public decimal ActiveCurrentBidPrice { get; set; }
            public decimal ActiveNetPnlUsd { get; set; }
            public decimal ActiveNetPnlPct { get; set; }
            public decimal TargetNetProfitPct { get; set; }
            
            public List<BinanceAssetScanEntity> MarketScanner { get; set; } = new List<BinanceAssetScanEntity>();
        }
    }

    // =========================================================================
    // 2. CAPA DATABASE: Conexión Privada a Binance y Firmas HMAC-SHA256
    // =========================================================================
    namespace DataBase
    {
        public class BillDB
        {
            private static readonly HttpClient _publicHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            private static HttpClient _privateHttp = new HttpClient();

            public string Symbol { get; set; } = "BTCUSDT";
            public string ApiStatus { get; set; } = "Iniciando...";
            public decimal RealUsdtBalance { get; set; } = 0m;
            public bool IsApiAuthenticated { get; set; } = false;

            public string BinanceApiKey => Environment.GetEnvironmentVariable("BINANCE_API_KEY", EnvironmentVariableTarget.User) 
                                        ?? Environment.GetEnvironmentVariable("BINANCE_API_KEY") ?? string.Empty;
            public string BinanceApiSecret => Environment.GetEnvironmentVariable("BINANCE_API_SECRET", EnvironmentVariableTarget.User) 
                                           ?? Environment.GetEnvironmentVariable("BINANCE_API_SECRET") ?? string.Empty;

            public BillDB()
            {
                if (!string.IsNullOrEmpty(BinanceApiKey))
                {
                    _privateHttp.DefaultRequestHeaders.Add("X-MBX-APIKEY", BinanceApiKey.Trim());
                }
            }

            public async Task<List<decimal>> GetPublicKlinesAsync()
            {
                var closes = new List<decimal>();
                try
                {
                    string url = $"https://api.binance.com/api/v3/klines?symbol={this.Symbol}&interval=1m&limit=35";
                    string json = await _publicHttp.GetStringAsync(url);
                    if (json.StartsWith("["))
                    {
                        using JsonDocument doc = JsonDocument.Parse(json);
                        foreach (JsonElement candle in doc.RootElement.EnumerateArray())
                        {
                            closes.Add(decimal.Parse(candle[4].GetString() ?? "0", CultureInfo.InvariantCulture));
                        }
                    }
                } catch { }
                return closes;
            }

            public async Task<(decimal Bid, decimal Ask)> GetRealOrderBookTickerAsync()
            {
                try
                {
                    string url = $"https://api.binance.com/api/v3/ticker/bookTicker?symbol={this.Symbol}";
                    string json = await _publicHttp.GetStringAsync(url);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    decimal bid = decimal.Parse(doc.RootElement.GetProperty("bidPrice").GetString() ?? "0", CultureInfo.InvariantCulture);
                    decimal ask = decimal.Parse(doc.RootElement.GetProperty("askPrice").GetString() ?? "0", CultureInfo.InvariantCulture);
                    return (bid, ask);
                }
                catch { return (0m, 0m); }
            }

            public async Task SyncRealWalletBalanceAsync()
            {
                if (string.IsNullOrEmpty(BinanceApiKey) || string.IsNullOrEmpty(BinanceApiSecret))
                {
                    this.ApiStatus = "⚠️ API Keys no detectadas. Corriendo en Modo Simulación.";
                    this.IsApiAuthenticated = false;
                    this.RealUsdtBalance = 50.00m; 
                    return;
                }

                try
                {
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string query = $"timestamp={timestamp}";
                    string signature = SignHmacSha256(query, BinanceApiSecret.Trim());
                    string url = $"https://api.binance.com/api/v3/account?{query}&signature={signature}";

                    string json = await _privateHttp.GetStringAsync(url);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement balances = doc.RootElement.GetProperty("balances");

                    foreach (JsonElement bal in balances.EnumerateArray())
                    {
                        if (bal.GetProperty("asset").GetString() == "USDT")
                        {
                            string freeStr = bal.GetProperty("free").GetString() ?? "0";
                            this.RealUsdtBalance = decimal.Parse(freeStr, CultureInfo.InvariantCulture);
                            this.IsApiAuthenticated = true;
                            this.ApiStatus = "🟢 Conectado a Billetera Binance Real";
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.ApiStatus = $"❌ Error de Autenticación API: {ex.Message}";
                    this.IsApiAuthenticated = false;
                }
            }

            public async Task<bool> ExecuteRealMarketOrderAsync(string symbol, string side, decimal quantityOrUsdt)
            {
                if (!IsApiAuthenticated) return true; 

                try
                {
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string qtyParam = side == "BUY" ? $"quoteOrderQty={quantityOrUsdt.ToString(CultureInfo.InvariantCulture)}" 
                                                    : $"quantity={quantityOrUsdt.ToString(CultureInfo.InvariantCulture)}";
                    
                    string query = $"symbol={symbol}&side={side}&type=MARKET&{qtyParam}&timestamp={timestamp}";
                    string signature = SignHmacSha256(query, BinanceApiSecret.Trim());
                    string url = $"https://api.binance.com/api/v3/order?{query}&signature={signature}";

                    var response = await _privateHttp.PostAsync(url, null);
                    return response.IsSuccessStatusCode;
                }
                catch
                {
                    return false;
                }
            }

            private string SignHmacSha256(string message, string secret)
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                using var hmac = new HMACSHA256(keyBytes);
                byte[] hash = hmac.ComputeHash(messageBytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    // =========================================================================
    // 3. CAPA BUSINESS: Estrategia Auto-Gestionada con el 100% del Saldo
    // =========================================================================
    namespace Business
    {
        using DataBase;
        using Entity;

        public class TradingBotBusiness
        {
            private readonly BillDB _billDb = new BillDB();
            private readonly object _lock = new object();

            private readonly string[] _pairs = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "DOGEUSDT" };

            private bool _isBotRunning = true;
            private decimal _profitVaultUsd = 0.00m;
            private int _winningTradesCount = 0;

            private string _activeSymbol = string.Empty;
            private decimal _activeUnits = 0.00m;
            private decimal _activeEntryAskPrice = 0.00m;
            private decimal _activeCurrentBidPrice = 0.00m;
            private decimal _activeTotalInvestedUsd = 0.00m;

            private decimal _activeDynamicTargetPct = 0.30m;

            private List<Entity.BinanceAssetScanEntity> _latestScan = new List<Entity.BinanceAssetScanEntity>();

            public async Task RunProductionCycleAsync()
            {
                if (string.IsNullOrEmpty(_activeSymbol))
                {
                    await _billDb.SyncRealWalletBalanceAsync();
                }

                var scanList = new List<Entity.BinanceAssetScanEntity>();

                foreach (var sym in _pairs)
                {
                    _billDb.Symbol = sym;
                    var closes = await _billDb.GetPublicKlinesAsync();
                    if (closes.Count < 20) continue;

                    var (bid, ask) = await _billDb.GetRealOrderBookTickerAsync();
                    if (bid == 0) bid = closes.Last();
                    if (ask == 0) ask = closes.Last();

                    decimal spreadPct = ((ask - bid) / bid) * 100m;
                    decimal rsi14 = CalculateRsi(closes, 14);
                    decimal volPct = CalculateVolatility(closes, 15);
                    decimal dynTarget = Math.Max(0.25m, volPct * 0.40m);

                    int passed = 0;
                    if (spreadPct <= 0.05m) passed++;
                    if (volPct >= 0.30m) passed++;
                    if (rsi14 >= 30m && rsi14 <= 50m) passed++;
                    if (closes[closes.Count - 1] > closes[closes.Count - 2]) passed++;

                    bool isSafe = (passed == 4);
                    string status = _activeSymbol == sym ? "⚡ INVERSIÓN ACTIVA" : (isSafe ? "✅ APROBADO" : "⏳ Filtrando...");

                    scanList.Add(new Entity.BinanceAssetScanEntity
                    {
                        Symbol = sym, BidPrice = bid, AskPrice = ask,
                        VolatilityPct = Math.Round(volPct, 2), Rsi14 = Math.Round(rsi14, 1),
                        DynamicTargetPct = Math.Round(dynTarget, 2), FiltersPassed = passed,
                        IsSafeEntry = isSafe, StatusText = status
                    });

                    if (_activeSymbol == sym) _activeCurrentBidPrice = bid;
                }

                lock (_lock) { _latestScan = scanList.OrderByDescending(x => x.IsSafeEntry).ToList(); }

                if (!_isBotRunning) return;

                if (string.IsNullOrEmpty(_activeSymbol))
                {
                    var best = _latestScan.FirstOrDefault(x => x.IsSafeEntry);
                    if (best != null && _billDb.RealUsdtBalance >= 10.00m)
                    {
                        await ExecuteFullBalanceBuyAsync(best);
                    }
                }
                else
                {
                    (decimal netUsd, decimal netPct) = CalculateRealNetPnl();
                    var current = _latestScan.FirstOrDefault(x => x.Symbol == _activeSymbol);
                    
                    if (netPct >= _activeDynamicTargetPct && netUsd > 0.05m)
                    {
                        await ExecuteFullBalanceSellAsync();
                    }
                    else if (current != null && current.Rsi14 >= 70m && netUsd > 0.05m)
                    {
                        await ExecuteFullBalanceSellAsync();
                    }
                }
            }

            private async Task ExecuteFullBalanceBuyAsync(Entity.BinanceAssetScanEntity candidate)
            {
                decimal capitalToInvest = _billDb.RealUsdtBalance;
                
                bool success = await _billDb.ExecuteRealMarketOrderAsync(candidate.Symbol, "BUY", capitalToInvest);
                if (!success) return;

                lock (_lock)
                {
                    _activeTotalInvestedUsd = capitalToInvest;
                    decimal fee = _activeTotalInvestedUsd * 0.0010m; 
                    decimal net = _activeTotalInvestedUsd - fee;
                    
                    _activeSymbol = candidate.Symbol;
                    _activeEntryAskPrice = candidate.AskPrice;
                    _activeCurrentBidPrice = candidate.BidPrice;
                    _activeUnits = net / candidate.AskPrice;
                    _activeDynamicTargetPct = candidate.DynamicTargetPct;
                    
                    _billDb.RealUsdtBalance = 0m; 
                }
            }

            public async Task ExecuteFullBalanceSellAsync()
            {
                if (string.IsNullOrEmpty(_activeSymbol)) return;

                bool success = await _billDb.ExecuteRealMarketOrderAsync(_activeSymbol, "SELL", Math.Round(_activeUnits, 5));
                
                lock (_lock)
                {
                    (decimal netUsd, decimal netPct) = CalculateRealNetPnl();
                    if (netUsd > 0)
                    {
                        _profitVaultUsd += netUsd;
                        _winningTradesCount++;
                    }

                    _activeSymbol = string.Empty;
                    _activeTotalInvestedUsd = 0m;
                    _activeUnits = 0m;
                }

                await _billDb.SyncRealWalletBalanceAsync();
            }

            public void ToggleBot(bool run) { lock (_lock) { _isBotRunning = run; } }

            public Entity.DashboardStateEntity GetStateSnapshot()
            {
                lock (_lock)
                {
                    bool hasPos = !string.IsNullOrEmpty(_activeSymbol);
                    (decimal netUsd, decimal netPct) = CalculateRealNetPnl();

                    return new Entity.DashboardStateEntity
                    {
                        IsBotRunning = _isBotRunning,
                        ApiHealthStatus = _billDb.ApiStatus,
                        RealBinanceUsdtBalance = Math.Round(_billDb.RealUsdtBalance, 2),
                        IsApiConnected = _billDb.IsApiAuthenticated,
                        
                        TotalActiveTradeCapital = Math.Round(hasPos ? _activeTotalInvestedUsd : _billDb.RealUsdtBalance, 2),
                        ProfitVaultUsd = Math.Round(_profitVaultUsd, 4),
                        WinningTradesCount = _winningTradesCount,
                        
                        HasActivePosition = hasPos,
                        ActiveSymbol = hasPos ? _activeSymbol : "Esperando Oportunidad Segura...",
                        ActiveEntryPrice = Math.Round(_activeEntryAskPrice, 4),
                        ActiveCurrentBidPrice = Math.Round(_activeCurrentBidPrice, 4),
                        ActiveNetPnlUsd = Math.Round(netUsd, 4),
                        ActiveNetPnlPct = Math.Round(netPct, 2),
                        TargetNetProfitPct = _activeDynamicTargetPct,
                        
                        MarketScanner = _latestScan.ToList()
                    };
                }
            }

            private (decimal NetUsd, decimal NetPct) CalculateRealNetPnl()
            {
                if (string.IsNullOrEmpty(_activeSymbol) || _activeTotalInvestedUsd <= 0m) return (0m, 0m);
                decimal netSale = (_activeUnits * _activeCurrentBidPrice) * (1m - 0.0010m);
                decimal netUsd = netSale - _activeTotalInvestedUsd;
                return (netUsd, (netUsd / _activeTotalInvestedUsd) * 100m);
            }

            private decimal CalculateVolatility(List<decimal> p, int lb) { var r = p.Skip(Math.Max(0, p.Count - lb)).ToList(); if (r.Count < 2) return 0m; decimal m = r.Min(); return m > 0 ? ((r.Max() - m) / m) * 100m : 0m; }
            private decimal CalculateRsi(List<decimal> p, int pd) { if (p.Count <= pd) return 50m; decimal g = 0m, l = 0m; for (int i = p.Count - pd; i < p.Count; i++) { decimal d = p[i] - p[i - 1]; if (d >= 0) g += d; else l -= d; } if (l == 0) return 100m; return 100m - (100m / (1m + ((g / pd) / (l / pd)))); }
        }
    }

    // =========================================================================
    // SERVIDOR WEB (Visualización Final V3)
    // =========================================================================
    class Program
    {
        private static readonly Business.TradingBotBusiness _bot = new Business.TradingBotBusiness();

        static async Task Main()
        {
            string url = "http://localhost:5050/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(url);
            listener.Start();

            Console.Title = "Binance V3 - Producción Full Automático (100% Saldo Billetera)";
            Console.WriteLine("============================================================================");
            Console.WriteLine(" PANEL DE PRODUCCIÓN: http://localhost:5050/");
            Console.WriteLine(" Escaneando llaves API en variables de entorno...");
            Console.WriteLine("============================================================================");

            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            _ = Task.Run(async () => { while (true) { try { await _bot.RunProductionCycleAsync(); } catch { } await Task.Delay(4000); } });
            while (true) { HttpListenerContext ctx = await listener.GetContextAsync(); _ = Task.Run(() => HandleHttpRequestAsync(ctx)); }
        }

        private static async Task HandleHttpRequestAsync(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            var query = ctx.Request.QueryString;
            try
            {
                if (path == "/api/state") { await WriteResponseAsync(ctx, JsonSerializer.Serialize(_bot.GetStateSnapshot()), "application/json"); }
                else if (path == "/api/toggle") { _bot.ToggleBot(query["run"] == "true"); await WriteResponseAsync(ctx, "{\"ok\":true}", "application/json"); }
                else if (path == "/api/sell") { await _bot.ExecuteFullBalanceSellAsync(); await WriteResponseAsync(ctx, "{\"ok\":true}", "application/json"); }
                else { await WriteResponseAsync(ctx, GetDashboardHtml(), "text/html; charset=utf-8"); }
            }
            catch (Exception ex) { await WriteResponseAsync(ctx, "{\"error\":\"" + ex.Message + "\"}", "application/json"); }
        }

        private static async Task WriteResponseAsync(HttpListenerContext ctx, string content, string type)
        {
            byte[] buf = Encoding.UTF8.GetBytes(content);
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }

        private static string GetDashboardHtml() => @"<!DOCTYPE html>
<html lang='es'><head><meta charset='UTF-8'><title>Binance V3 - Producción</title>
<style>
  body { font-family:system-ui; background:#070a12; color:#f8fafc; margin:0; padding:16px; }
  .header { display:flex; justify-content:space-between; background:#111726; padding:14px; border-radius:12px; border:1px solid #1f293d; margin-bottom:14px; }
  .grid-top { display:grid; grid-template-columns:repeat(3, 1fr); gap:14px; margin-bottom:14px; }
  .card { background:#111726; border:1px solid #1f293d; border-radius:12px; padding:15px; }
  .val { font-size:26px; font-weight:800; }
  .pos-banner { background:#151f33; border:1px solid #3b82f6; border-radius:12px; padding:16px; margin-bottom:14px; }
  table { width:100%; border-collapse:collapse; font-size:13px; } th, td { padding:10px; border-bottom:1px solid #1f293d; text-align:left; }
  button { padding:10px 16px; border-radius:8px; font-weight:bold; cursor:pointer; border:none; color:#fff; }
</style></head>
<body>
  <div class='header'>
    <div><h2 style='margin:0;'>🟡 Binance Spot V3 (Sincronización Total Billetera)</h2><small id='sourceTxt' style='color:#94a3b8;'>Conectando...</small></div>
    <div>
      <button style='background:#10b981;' onclick='toggleBot(true)'>▶ Iniciar Bot</button>
      <button style='background:#ef4444;' onclick='toggleBot(false)'>⏸ Pausar</button>
    </div>
  </div>

  <div class='grid-top'>
    <div class='card' style='border:1px solid #38bdf8;'>
      <h4 style='margin:0;color:#93c5fd;'>🏦 SALDO USDT LIBRE EN BINANCE</h4>
      <div class='val' id='walletVal' style='color:#38bdf8;'>$0.00</div>
      <small id='walletSub' style='color:#cbd5e1;'>El bot usa el 100% automático.</small>
    </div>
    <div class='card'>
      <h4 style='margin:0;color:#6ee7b7;'>💰 BÓVEDA DE GANANCIAS NETAS</h4>
      <div class='val' id='vaultVal' style='color:#10b981;'>+$0.0000</div>
      <small id='vaultSub'>0 cosechas reales.</small>
    </div>
    <div class='card'>
      <h4 style='margin:0;color:#94a3b8;'>📊 ESTADO OPERACIÓN ACTUAL</h4>
      <div class='val' id='livePnlVal'>$0.0000</div>
      <small id='livePnlSub'>Esperando entrada.</small>
    </div>
  </div>

  <div class='pos-banner'>
    <h3 id='posTitle' style='margin:0 0 6px 0;'>Escaneando...</h3>
    <div id='posPrices' style='color:#cbd5e1; margin-bottom:12px;'>Esperando oportunidad para invertir tu saldo.</div>
    <button id='btnSmartSell' onclick='forceSell()' style='background:#334155; width:100%;'>⏳ Monitoreando Mercado</button>
  </div>

  <div class='card'>
    <h3 style='margin:0 0 10px 0;'>Radar Cripto 24/7 (Alta Liquidez)</h3>
    <table>
      <thead><tr><th>Par</th><th>Ask / Bid</th><th>Volatilidad</th><th>RSI (14)</th><th>Meta Dinámica</th><th>Filtros (Max 4)</th><th>Estado</th></tr></thead>
      <tbody id='scannerBody'></tbody>
    </table>
  </div>

<script>
async function refresh() {
  try {
    const r = await fetch('/api/state'); const s = await r.json();
    document.getElementById('sourceTxt').innerText = s.ApiHealthStatus;
    
    document.getElementById('walletVal').innerText = '$' + s.RealBinanceUsdtBalance.toFixed(2);
    document.getElementById('walletSub').innerText = s.IsApiConnected ? 'Sincronizado con tu cuenta.' : 'Variables de entorno no detectadas.';
    
    document.getElementById('vaultVal').innerText = '+$' + s.ProfitVaultUsd.toFixed(4);
    document.getElementById('vaultSub').innerText = s.WinningTradesCount + ' ventas exitosas.';

    const pnlEl = document.getElementById('livePnlVal'); 
    pnlEl.innerText = (s.ActiveNetPnlUsd >= 0 ? '+$' : '-$') + Math.abs(s.ActiveNetPnlUsd).toFixed(4) + ' (' + (s.ActiveNetPnlPct>=0?'+':'') + s.ActiveNetPnlPct.toFixed(2) + '%)'; 
    pnlEl.style.color = s.ActiveNetPnlUsd >= 0 ? '#10b981' : '#f59e0b';

    if (s.HasActivePosition) {
      document.getElementById('posTitle').innerText = 'Invirtiendo $' + s.TotalActiveTradeCapital.toFixed(2) + ' en ' + s.ActiveSymbol;
      document.getElementById('posPrices').innerHTML = 'Entrada: <b>$' + s.ActiveEntryPrice + '</b> | Precio Actual: <b style=\'color:#38bdf8;\'>$' + s.ActiveCurrentBidPrice + '</b> | Meta Venta: <b>+' + s.TargetNetProfitPct + '%</b>';
      const btn = document.getElementById('btnSmartSell');
      if (s.ActiveNetPnlUsd > 0) { btn.style.background = '#10b981'; btn.innerText = '💰 Forzar Cosecha (+$' + s.ActiveNetPnlUsd.toFixed(4) + ')'; } 
      else { btn.style.background = '#475569'; btn.innerText = '⏳ Esperando que suba...'; }
    } else {
      document.getElementById('posTitle').innerText = 'Tu dinero está en USDT seguro.';
      document.getElementById('posPrices').innerText = 'Buscando el momento exacto para comprar...';
    }
    
    let scanHtml = '';
    s.MarketScanner.forEach(c => {
      const col = c.StatusText.includes('APROBADO') ? '#10b981' : (c.StatusText.includes('INVERSIÓN') ? '#38bdf8' : '#94a3b8');
      scanHtml += `<tr><td><b>${c.Symbol}</b></td><td>$${c.AskPrice} / $${c.BidPrice}</td>
      <td>${c.VolatilityPct}%</td><td><b style='color:${c.Rsi14<=50?'#10b981':'#f59e0b'};'>${c.Rsi14}</b></td>
      <td>+${c.DynamicTargetPct}%</td><td><b>${c.FiltersPassed}/4</b></td>
      <td style='color:${col};font-weight:700;'>${c.StatusText}</td></tr>`;
    }); document.getElementById('scannerBody').innerHTML = scanHtml;
  } catch(e) {}
}
async function toggleBot(run) { await fetch('/api/toggle?run=' + run); refresh(); }
async function forceSell() { if(confirm('¿Vender al instante a precio de mercado?')) await fetch('/api/sell'); refresh(); }
setInterval(refresh, 2000); refresh();
</script></body></html>";
    }
}
