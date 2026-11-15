using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Miningcore.Configuration;

namespace Miningcore.Api.AdminPanel;

public static class AdminPanelEndpoints
{
    private const string CookieName = "mcce_session";
    private static readonly ConcurrentDictionary<string, (int failures, DateTime? bannedUntil)> loginTracker = new();

    /// <summary>
    /// Register admin routes on a standalone web application (separate port).
    /// </summary>
    public static void MapAdminPanel(this WebApplication app, ClusterConfig clusterConfig, int apiPort)
    {
        var cfg = clusterConfig.AdminPanel;
        if(cfg?.Enabled != true)
            return;

        var apiBase = $"http://localhost:{apiPort}";

        // Auth middleware — everything except the login page needs a valid session, including the
        // proxied /api routes: the proxy calls the API from localhost, which the admin IP whitelist trusts,
        // so an unauthenticated /api/admin/* request would otherwise reach payment and logging controls.
        app.Use(async (ctx, next) =>
        {
            if(!ctx.Request.Path.StartsWithSegments("/admin/login") && !IsAuthenticated(ctx, cfg))
            {
                if(ctx.Request.Path.StartsWithSegments("/api"))
                    ctx.Response.StatusCode = 401;
                else
                    ctx.Response.Redirect("/admin/login");
                return;
            }
            await next();
        });

        var admin = app.MapGroup("/admin");

        // Proxy API endpoints from the main host so the dashboard can fetch data
        app.MapGet("/api/pools", async ctx => await ProxyToApi(ctx, apiBase));
        app.MapGet("/api/pools/{**rest}", async (string rest, HttpContext ctx) => await ProxyToApi(ctx, apiBase));
        app.MapGet("/api/blocks", async ctx => await ProxyToApi(ctx, apiBase));
        app.MapGet("/api/admin/{**rest}", async (string rest, HttpContext ctx) => await ProxyToApi(ctx, apiBase));
        app.MapPost("/api/admin/{**rest}", async (string rest, HttpContext ctx) => await ProxyToApi(ctx, apiBase));

        // === Login page (HTML) ===
        admin.MapGet("/login", () =>
        {
            return Results.Content(LoginPageHtml, "text/html");
        });

        // === Login action ===
        admin.MapPost("/login", async (HttpContext ctx) =>
        {
            var ip = ClientIp(ctx);

            if(loginTracker.TryGetValue(ip, out var entry) && entry.bannedUntil > DateTime.UtcNow)
            {
                ctx.Response.StatusCode = 429;
                await ctx.Response.WriteAsync("Too many attempts. Try again later.");
                return;
            }

            var form = await ctx.Request.ReadFormAsync();
            var password = form["password"].ToString();

            if(!string.IsNullOrEmpty(cfg.Password) && CryptographicOperations.FixedTimeEquals(
                   Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(cfg.Password)))
            {
                loginTracker.TryRemove(ip, out _);

                var sessionData = $"{ip}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var signature = Sign(sessionData, cfg.Password);
                var cookieValue = $"{sessionData}|{signature}";

                ctx.Response.Cookies.Append(CookieName, cookieValue, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = ctx.Request.IsHttps || ctx.Request.Headers["X-Forwarded-Proto"] == "https",
                    MaxAge = TimeSpan.FromSeconds(cfg.SessionTimeout)
                });

                ctx.Response.Redirect("/admin");
            }
            else
            {
                var failures = (entry.failures + 1);
                var banned = failures >= cfg.MaxLoginAttempts
                    ? DateTime.UtcNow.AddSeconds(cfg.LoginBanDuration)
                    : (DateTime?) null;

                loginTracker.AddOrUpdate(ip,
                    _ => (failures, banned),
                    (_, _) => (failures, banned));

                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Wrong password.");
            }
        });

        // === Logout ===
        admin.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(CookieName);
            ctx.Response.Redirect("/admin/login");
        });

        // === Dashboard (HTML) ===
        admin.MapGet("/", (HttpContext ctx) =>
        {
            return Results.Content(DashboardHtml, "text/html");
        });
    }

    private static bool IsAuthenticated(HttpContext ctx, AdminPanelConfig cfg)
    {
        if(string.IsNullOrEmpty(cfg.Password))
            return true;

        if(!ctx.Request.Cookies.TryGetValue(CookieName, out var cookie))
            return false;

        var parts = cookie.Split('|');
        if(parts.Length != 3)
            return false;

        var ip = parts[0];
        var tsStr = parts[1];
        var providedSig = parts[2];

        if(ip != ClientIp(ctx))
            return false;

        if(long.TryParse(tsStr, out var ts))
        {
            var sessionTime = DateTimeOffset.FromUnixTimeSeconds(ts);
            if((DateTimeOffset.UtcNow - sessionTime).TotalSeconds > cfg.SessionTimeout)
                return false;
        }
        else return false;

        var expectedSig = Sign($"{ip}|{tsStr}", cfg.Password);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSig),
            Encoding.UTF8.GetBytes(expectedSig));
    }

    /// <summary>
    /// The visitor's address. When the panel sits behind a local reverse proxy (nginx on the same
    /// host), use the X-Real-IP header it sets so sessions and login bans are per visitor.
    /// </summary>
    private static string ClientIp(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;

        if(remote != null && System.Net.IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            var realIp = ctx.Request.Headers["X-Real-IP"].ToString();

            if(!string.IsNullOrEmpty(realIp))
                return realIp;
        }

        return remote?.ToString() ?? "unknown";
    }

    private static string Sign(string data, string key)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // long timeout: a Quantus send waits for the transaction to be included in a block
    private static readonly HttpClient apiClient = new() { Timeout = TimeSpan.FromMinutes(15) };

    private static async Task ProxyToApi(HttpContext ctx, string apiBase)
    {
        var url = $"{apiBase}{ctx.Request.Path}{ctx.Request.QueryString}";
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
        req.Headers.TryAddWithoutValidation("X-Admin-Client-Ip", ClientIp(ctx));

        if(ctx.Request.Body.CanRead && ctx.Request.Method != "GET")
        {
            req.Content = new StreamContent(ctx.Request.Body);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType ?? "application/json");
        }

        using var resp = await apiClient.SendAsync(req);
        ctx.Response.StatusCode = (int) resp.StatusCode;
        ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        await resp.Content.CopyToAsync(ctx.Response.Body);
    }

    private const string LoginPageHtml = @"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>MCCE Admin</title>
<style>*{margin:0;padding:0;box-sizing:border-box}body{background:#0d1117;color:#e6edf3;font:16px system-ui,sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh}.box{background:#161b22;border:1px solid #30363d;border-radius:12px;padding:36px;width:380px;box-shadow:0 8px 24px rgba(0,0,0,.3)}h1{font-size:22px;margin-bottom:6px;color:#e6edf3}.desc{color:#8b949e;font-size:13px;margin-bottom:24px}input{width:100%;padding:12px 14px;background:#0d1117;border:1px solid #30363d;border-radius:8px;color:#e6edf3;font-size:15px;margin-bottom:16px}input:focus{outline:none;border-color:#58a6ff;box-shadow:0 0 0 3px rgba(88,166,255,.15)}button{width:100%;padding:12px;background:#238636;color:#fff;border:0;border-radius:8px;font-size:15px;font-weight:600;cursor:pointer}button:hover{background:#2ea043}</style></head>
<body><div class=""box""><h1>⚡ MCCE Admin</h1><p class=""desc"">Enter your admin password to continue</p><form method=""post""><input type=""password"" name=""password"" placeholder=""Password"" autofocus><button type=""submit"">Sign in</button></form></div></body></html>";

    private const string DashboardHtml = @"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>MCCE Admin</title>
<style>
:root{--bg:#0d1117;--card:#161b22;--border:#30363d;--text:#e6edf3;--muted:#8b949e;--accent:#f78166;--green:#3fb950;--red:#f85149;--yellow:#d29922}
*{margin:0;padding:0;box-sizing:border-box}
body{background:var(--bg);color:var(--text);font:13px/1.5 system-ui,-apple-system,sans-serif;display:flex;min-height:100vh}
#sidebar{width:220px;background:var(--card);border-right:1px solid var(--border);padding:20px 0;position:fixed;top:0;left:0;bottom:0;overflow-y:auto}
#sidebar .logo{font-size:18px;font-weight:700;color:var(--text);padding:0 20px 20px;border-bottom:1px solid var(--border);margin-bottom:12px}
#sidebar a{display:flex;align-items:center;gap:10px;padding:10px 20px;color:var(--muted);text-decoration:none;font-size:13px;transition:all .15s}
#sidebar a:hover,#sidebar a.active{color:var(--text);background:rgba(255,255,255,.04)}
#sidebar a.active{border-right:2px solid var(--accent)}
#sidebar a .icon{font-size:16px;width:20px;text-align:center}
#main{margin-left:220px;flex:1;padding:0}
#topbar{background:var(--card);border-bottom:1px solid var(--border);padding:12px 24px;display:flex;justify-content:space-between;align-items:center;position:sticky;top:0;z-index:10}
#topbar h1{font-size:15px;font-weight:600;color:var(--text)}
#topbar .right{display:flex;align-items:center;gap:16px;font-size:12px;color:var(--muted)}
#topbar a{color:var(--muted);text-decoration:none}#topbar a:hover{color:var(--text)}
#content{padding:24px;max-width:1400px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:14px;margin-bottom:24px}
.stat{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:18px 20px}
.stat .top{display:flex;align-items:center;gap:10px;margin-bottom:10px}
.stat .icon{font-size:20px}
.stat .label{font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:.5px}
.stat .value{font-size:26px;font-weight:700;color:var(--text);margin-top:2px}
.stat .sub{font-size:11px;color:var(--muted);margin-top:4px}
.panel{background:var(--card);border:1px solid var(--border);border-radius:10px;margin-bottom:16px}
.panel-header{padding:14px 20px;border-bottom:1px solid var(--border);font-size:14px;font-weight:600;display:flex;justify-content:space-between;align-items:center}
.panel-body{padding:20px}
table{width:100%;border-collapse:collapse}
th{text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:.5px;color:var(--muted);padding:10px 16px;border-bottom:1px solid var(--border)}
td{padding:10px 16px;border-bottom:1px solid rgba(48,54,61,.5);font-size:13px}
tr:hover td{background:rgba(255,255,255,.02)}
.green{color:var(--green)}.red{color:var(--red)}.yellow{color:var(--yellow)}
.badge{display:inline-block;padding:2px 8px;border-radius:12px;font-size:11px;font-weight:500}
.badge-ok{background:rgba(63,185,80,.15);color:var(--green)}
.badge-warn{background:rgba(210,153,34,.15);color:var(--yellow)}
input,select{background:var(--bg);border:1px solid var(--border);border-radius:6px;color:var(--text);padding:8px 12px;font-size:13px;width:100%}
input:focus{outline:none;border-color:#58a6ff}
#content a{color:#58a6ff}.btn:disabled{opacity:.45;cursor:not-allowed}
.btn{padding:8px 16px;border-radius:6px;font-size:13px;cursor:pointer;border:0;font-weight:500}
.btn-primary{background:#238636;color:#fff}.btn-primary:hover{background:#2ea043}
.btn-secondary{background:var(--bg);color:var(--text);border:1px solid var(--border)}.btn-secondary:hover{background:#21262d}
.btn-danger{background:rgba(248,81,73,.15);color:var(--red);border:1px solid rgba(248,81,73,.3)}.btn-danger:hover{background:rgba(248,81,73,.25)}
.row{display:flex;gap:16px;margin-bottom:16px}.col{flex:1}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:16px}
.loading{color:var(--muted);text-align:center;padding:40px}
@media(max-width:768px){#sidebar{display:none}#main{margin-left:0}}
</style></head>
<body>
<div id=""sidebar"">
 <div class=""logo"">⚡ MCCE Admin</div>
 <a href=""#"" class=""active"" onclick=""t('overview')""><span class=""icon"">📊</span> Overview</a>
 <a href=""#"" onclick=""t('pools')""><span class=""icon"">⛏️</span> Pools</a>
 <a href=""#"" onclick=""t('miners')""><span class=""icon"">👤</span> Miners</a>
 <a href=""#"" onclick=""t('blocks')""><span class=""icon"">🧱</span> Blocks</a>
 <a href=""#"" onclick=""t('payments')""><span class=""icon"">💰</span> Payments</a>
<a href=""#"" onclick=""t('wallets')""><span class=""icon"">👛</span> Wallets</a>
 <a href=""#"" onclick=""t('settings')""><span class=""icon"">⚙️</span> Settings</a>
</div>
<div id=""main"">
 <div id=""topbar""><h1 id=""page-title"">Overview</h1><div class=""right""><span id=""clock""></span><a href=""/admin/logout"">Logout</a></div></div>
 <div id=""content""><div class=""loading"">Loading...</div></div>
</div>
<script>
setInterval(function(){document.getElementById('clock').textContent=new Date().toLocaleString()},1000);
document.getElementById('clock').textContent=new Date().toLocaleString();

function t(n){
 document.querySelectorAll('#sidebar a').forEach(a=>a.classList.remove('active'));
 event.target.closest('a').classList.add('active');
 document.getElementById('page-title').textContent=event.target.closest('a').textContent.trim();
 document.getElementById('content').innerHTML='<div class=""loading"">Loading...</div>';
 loaders[n]();
}
async function a(u){var r=await fetch(u);if(r.status==401){location.href='/admin/login';throw new Error('login')}if(!r.ok)throw new Error(u+' '+r.status);return r.json()}
function f(n,d=2){return n!=null?Number(n).toFixed(d):'0'}
function h(v){if(!v)return'0 H/s';if(v>1e15)return f(v/1e15,2)+' PH/s';if(v>1e12)return f(v/1e12,2)+' TH/s';if(v>1e9)return f(v/1e9,2)+' GH/s';if(v>1e6)return f(v/1e6,2)+' MH/s';return f(v/1e3,2)+' KH/s'}
function ago(d){if(!d)return'—';var s=(Date.now()-new Date(d).getTime())/1000;if(s<60)return Math.round(s)+'s ago';if(s<3600)return Math.round(s/60)+'m ago';if(s<86400)return Math.round(s/3600)+'h ago';return Math.round(s/86400)+'d ago'}
function E(s){var t='',l=String(s||'').toLowerCase();if(l=='confirmed')t='badge-ok';else if(l=='pending')t='badge-warn';return '<span class=""badge '+t+'"">'+s+'</span>';}

function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]})}
function sendForm(coin){
 var x=(window._w||[]).find(function(y){return y.coin==coin});if(!x)return;
 var box=document.getElementById('sendbox');
 box.innerHTML='<div class=""panel""><div class=""panel-header"">Send '+esc(coin)+' from the pool wallet</div><div class=""panel-body"">'+
  '<p class=""muted"" style=""margin-bottom:12px"">Available '+f(x.available,6)+' '+esc(coin)+' · free (not owed to miners) '+f(x.free,6)+' '+esc(coin)+'</p>'+
  '<div class=""row""><div class=""col""><label class=""muted"">To address</label><input id=""sa"" autocomplete=""off"" spellcheck=""false""></div></div>'+
  '<div class=""row""><div class=""col""><label class=""muted"">Amount ('+esc(coin)+') <a href=""#"" id=""smax"">use free balance</a></label><input id=""sm"" inputmode=""decimal"" autocomplete=""off""></div>'+
  '<div class=""col""><label class=""muted"">Admin password</label><input id=""sp"" type=""password"" autocomplete=""current-password""></div></div>'+
  '<button class=""btn btn-primary"" id=""sgo"">Send</button> <button class=""btn btn-secondary"" id=""scancel"">Cancel</button><div id=""sres"" style=""margin-top:14px""></div></div></div>';
 box.scrollIntoView({behavior:'smooth'});
 document.getElementById('smax').addEventListener('click',function(e){e.preventDefault();document.getElementById('sm').value=Math.max(0,Math.floor((x.free||0)*1e6)/1e6)});
 document.getElementById('scancel').addEventListener('click',function(){box.innerHTML=''});
 document.getElementById('sgo').addEventListener('click',async function(){
  var btn=this,res=document.getElementById('sres');
  var addr=document.getElementById('sa').value.trim(),amt=parseFloat(document.getElementById('sm').value),pw=document.getElementById('sp').value;
  if(!addr||!(amt>0)||!pw){res.innerHTML='<span style=""color:var(--red)"">Fill in the address, an amount above 0 and your password.</span>';return}
  if(amt>(x.available||0)){res.innerHTML='<span style=""color:var(--red)"">That is more than the wallet has available.</span>';return}
  var warn=amt>(x.free||0)?'\n\nWARNING: this is more than the free balance. The pool will not have enough left to pay miners what they are owed.':'';
  if(!confirm('Send '+amt+' '+coin+' to\n'+addr+' ?\n\nThis cannot be undone.'+warn))return;
  btn.disabled=true;btn.textContent='Sending…';res.innerHTML='<span class=""muted"">Sending'+(coin=='QTC'?' (Quantus waits for the block, this can take a minute)':'')+'…</span>';
  try{
   var r=await fetch('/api/admin/wallets/'+encodeURIComponent(coin)+'/send',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({address:addr,amount:amt,password:pw})});
   if(r.status==401){location.href='/admin/login';return}
   var d=await r.json().catch(function(){return{}});
   if(r.ok){document.getElementById('sp').value='';res.innerHTML='<span style=""color:var(--green)"">Sent.</span> Transaction: '+(d.explorerUrl?'<a href=""'+esc(d.explorerUrl)+'"" target=""_blank"" rel=""noopener"">'+esc(d.txId)+'</a>':esc(d.txId));setTimeout(function(){loaders.wallets().then(function(){})},4000);}
   else{res.innerHTML='<span style=""color:var(--red)"">Not sent: '+esc(d.error||('error '+r.status))+'</span>';}
  }catch(e){res.innerHTML='<span style=""color:var(--red)"">No answer from the server. Check the wallet balance before trying again.</span>';}
  btn.disabled=false;btn.textContent='Send';
 });
}
var loaders={
overview:async function(){
 var d=await a('/api/pools');
 if(!d.pools||!d.pools.length){document.getElementById('content').innerHTML='<div class=""loading"">No pools configured</div>';return}
 var html='<div class=""stats"">';
 for(var p of d.pools||[]){var s=p.poolStats||{},n=p.networkStats||{};
  html+='<div class=""stat""><div class=""top""><span class=""icon"">⛏️</span><span class=""label"">'+p.id+' Hashrate</span></div><div class=""value"">'+h(s.poolHashrate||0)+'</div><div class=""sub"">'+s.connectedMiners+' miners · '+f(s.sharesPerSecond||0)+' shares/s</div></div>';
  html+='<div class=""stat""><div class=""top""><span class=""icon"">🌐</span><span class=""label"">Network</span></div><div class=""value"">'+h(n.networkHashrate||0)+'</div><div class=""sub"">Height '+n.blockHeight+' · Diff '+(n.networkDifficulty?f(n.networkDifficulty/1e9,1)+'G':'—')+'</div></div>';
  html+='<div class=""stat""><div class=""top""><span class=""icon"">🔌</span><span class=""label"">'+p.id+' Ports</span></div><div class=""value"" style=""font-size:20px"">'+Object.keys(p.ports||{}).join(', ')+'</div><div class=""sub"">Listening</div></div>';
  html+='<div class=""stat""><div class=""top""><span class=""icon"">💵</span><span class=""label"">Fee</span></div><div class=""value"">'+f(p.poolFeePercent||0,1)+'%</div><div class=""sub"">Pool fee</div></div>';
 }
 html+='</div>';
 html+='<div class=""panel""><div class=""panel-header"">Recent Blocks</div><div class=""panel-body""><table><tr><th>Pool</th><th>Height</th><th>Status</th><th>Effort</th><th>Miner</th><th>When</th></tr>';
 try{
  var blocks=[],seen=new Set();
  for(var p of d.pools||[]){var b=await a('/api/pools/'+p.id+'/blocks?pageSize=8');for(var x of b||[]){var k=p.id+'-'+x.blockHeight;if(!seen.has(k)){seen.add(k);blocks.push({...x,pool:p.id});}}}
  blocks.sort((a,b)=>new Date(b.created)-new Date(a.created));
  for(var x of blocks.slice(0,10)){html+='<tr><td>'+x.pool+'</td><td>'+x.blockHeight+'</td><td>'+E(x.status)+'</td><td>'+f((x.effort||0)*100,1)+'%</td><td>'+(x.miner||'—').slice(0,14)+'</td><td>'+ago(x.created)+'</td></tr>';}
 }catch(e){}
 html+='</table></div></div>';
 document.getElementById('content').innerHTML=html;
},
pools:async function(){
 var d=await a('/api/pools'),html='';
 for(var p of d.pools||[]){var s=p.poolStats||{},n=p.networkStats||{};
  html+='<div class=""panel""><div class=""panel-header"">'+p.id+' <span class=""badge badge-ok"">Online</span></div><div class=""panel-body"">';
  html+='<div class=""stats"" style=""margin-bottom:0"">';
  html+='<div class=""stat""><div class=""label"">Pool Hashrate</div><div class=""value"">'+h(s.poolHashrate||0)+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Connected Miners</div><div class=""value"">'+s.connectedMiners+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Network Hashrate</div><div class=""value"">'+h(n.networkHashrate||0)+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Network Difficulty</div><div class=""value"">'+(n.networkDifficulty?f(n.networkDifficulty/1e9,1)+'G':'—')+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Block Height</div><div class=""value"">'+n.blockHeight+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Ports</div><div class=""value"" style=""font-size:18px"">'+Object.keys(p.ports||{}).join(', ')+'</div></div>';
  html+='</div></div></div>';
 }
 document.getElementById('content').innerHTML=html||'<div class=""loading"">No pools</div>';
},
miners:async function(){
 document.getElementById('content').innerHTML='<div class=""panel""><div class=""panel-header"">Search Miner</div><div class=""panel-body""><div class=""row""><div class=""col""><input id=""s"" placeholder=""Miner address or wallet..."" autofocus></div><div class=""col"" style=""flex:0""><button class=""btn btn-primary"" onclick=""search()"">Search</button></div></div><div id=""r""></div></div></div>';
 window.search=async function(){
  var addr=document.getElementById('s').value.trim();if(!addr)return;
  var d=await a('/api/pools'),html='<table><tr><th>Pool</th><th>Hashrate</th><th>Shares/s</th><th>Workers</th><th>Pending</th><th>Paid</th></tr>';
  for(var p of d.pools||[]){
   try{var m=await a('/api/pools/'+p.id+'/miners/'+addr);
    var hr=0,sps=0,ws=(m.performance&&m.performance.workers)||{};for(var w in ws){hr+=ws[w].hashrate||0;sps+=ws[w].sharesPerSecond||0;}
    html+='<tr><td>'+p.id+'</td><td>'+h(hr)+'</td><td>'+f(sps,2)+'</td><td>'+Object.keys(ws).length+'</td><td>'+f(m.pendingBalance||0,6)+'</td><td>'+f(m.totalPaid||0,6)+'</td></tr>';
   }catch(e){html+='<tr><td>'+p.id+'</td><td colspan=""5"" class=""muted"">Not found</td></tr>';}
  }
  html+='</table>';document.getElementById('r').innerHTML=html||'<p>No data</p>';
 };
},
blocks:async function(){
 var d=await a('/api/pools'),html='';
 for(var p of d.pools||[]){
  try{
   var b=await a('/api/pools/'+p.id+'/blocks?pageSize=15');
   html+='<div class=""panel""><div class=""panel-header"">'+p.id+' <span style=""color:var(--muted);font-weight:400;font-size:12px"">last 15</span></div><div class=""panel-body""><table><tr><th>Height</th><th>Status</th><th>Effort</th><th>Miner</th><th>Reward</th><th>When</th></tr>';
   for(var x of b||[]){html+='<tr><td>'+x.blockHeight+'</td><td>'+E(x.status)+'</td><td>'+f((x.effort||0)*100,1)+'%</td><td>'+(x.miner||'—').slice(0,16)+'</td><td>'+f(x.reward||0,6)+'</td><td>'+ago(x.created)+'</td></tr>';}
   html+='</table></div></div>';
  }catch(e){}
 }
 document.getElementById('content').innerHTML=html||'<div class=""loading"">No blocks found</div>';
},
payments:async function(){
 var d=await a('/api/pools'),html='';
 for(var p of d.pools||[]){
  try{
   var pp=await a('/api/pools/'+p.id+'/payments?pageSize=15');
   html+='<div class=""panel""><div class=""panel-header"">'+p.id+' <span style=""color:var(--muted);font-weight:400;font-size:12px"">last 15</span></div><div class=""panel-body""><table><tr><th>Address</th><th>Amount</th><th>When</th></tr>';
   for(var x of pp||[]){html+='<tr><td>'+(x.address||'—').slice(0,20)+'...</td><td>'+f(x.amount,6)+'</td><td>'+ago(x.created)+'</td></tr>';}
   html+='</table></div></div>';
  }catch(e){}
 }
 document.getElementById('content').innerHTML=html||'<div class=""loading"">No payments</div>';
},
settings:async function(){
 var html='<div class=""grid2"">';
 html+='<div class=""panel""><div class=""panel-header"">Garbage Collection</div><div class=""panel-body"">';
 try{var g=await a('/api/admin/stats/gc');html+='<table><tr><th>Gen</th><th>Collections</th></tr><tr><td>Gen 0</td><td>'+g.gcGen0+'</td></tr><tr><td>Gen 1</td><td>'+g.gcGen1+'</td></tr><tr><td>Gen 2</td><td>'+g.gcGen2+'</td></tr></table><p style=""margin-top:12px""><strong>Managed Memory:</strong> '+(typeof g.memAllocated=='number'?(g.memAllocated/1024/1024).toFixed(1)+' MB':esc(g.memAllocated))+'</p><p style=""margin-top:8px""><button class=""btn btn-secondary"" onclick=""fetch(\'/api/admin/forcegc\',{method:\'POST\'}).then(r=>alert(r.ok?\'GC triggered\':\'Failed\'))"">Force GC</button></p>';}catch(e){html+='<p class=""muted"">Unable to load GC stats</p>';}
 html+='</div></div>';
 html+='<div class=""panel""><div class=""panel-header"">System</div><div class=""panel-body""><table>';
 try{var d=await a('/api/pools');var n=d.pools[0].networkStats||{};
  html+='<tr><td>Coin</td><td>'+(d.pools[0].coin?.name||'—')+'</td></tr>';
  html+='<tr><td>Difficulty</td><td>'+(n.networkDifficulty?f(n.networkDifficulty/1e9,1)+'G':'—')+'</td></tr>';
  html+='<tr><td>Block Height</td><td>'+n.blockHeight+'</td></tr>';
 }catch(e){}
 html+='<tr><td>Build</td><td>MCCE .NET 8</td></tr>';
 html+='</table></div></div>';
 html+='</div>';
 document.getElementById('content').innerHTML=html;
},
wallets:async function(){
 var w=await a('/api/admin/wallets');window._w=w;
 var html='<div class=""panel""><div class=""panel-header"">Pool wallets <span class=""muted"" style=""font-weight:400;font-size:12px"">Free = available minus what the pool owes miners</span></div><div class=""panel-body"" style=""overflow-x:auto""><table><tr><th>Coin</th><th>Available</th><th>Pending</th><th>Owed to miners</th><th>Free</th><th>Address</th><th></th></tr>';
 for(var x of w){
  var pend=(x.pending||0)+(x.immature||0);
  html+='<tr><td><strong>'+x.coin+'</strong><div class=""muted"" style=""font-size:11px"">'+x.pools.join(', ')+'</div></td>';
  if(x.error){html+='<td colspan=""4"" style=""color:var(--red)"">'+esc(x.error)+'</td>';}
  else{html+='<td>'+f(x.available,6)+'</td><td>'+(pend?f(pend,6):'—')+'</td><td>'+f(x.owedToMiners,6)+'</td><td style=""color:'+(x.free<0?'var(--red)':'var(--green)')+'"">'+f(x.free,6)+'</td>';}
  html+='<td class=""muted"" title=""'+esc(x.address)+'"">'+esc((x.address||'').slice(0,12))+'…</td><td><button class=""btn btn-secondary"" data-send=""'+x.coin+'""'+(x.error?' disabled':'')+'>Send</button></td></tr>';
 }
 html+='</table></div></div><div id=""sendbox""></div>';
 document.getElementById('content').innerHTML=html;
 document.querySelectorAll('[data-send]').forEach(function(b){b.addEventListener('click',function(){sendForm(b.getAttribute('data-send'))})});
}
};
loaders.overview();
</script></body></html>";
}
