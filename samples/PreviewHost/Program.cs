using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using PreviewHost;
using PreviewHost.Components;
using SenX.Monitor.Pages;
using SenX.Monitor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<IMonitorServers, SimulatedServers>();
builder.Services.AddMonitorUi(Path.Combine(Path.GetTempPath(), "monitor-preview"), "_content/SenX.Monitor/");

// Quasar defines these. Here everything is allowed, because there's nothing to protect.
builder.Services.AddAuthorizationCore(options =>
{
    options.AddPolicy(MonitorPolicies.View, policy => policy.RequireAssertion(_ => true));
    options.AddPolicy(MonitorPolicies.Control, policy => policy.RequireAssertion(_ => true));
    options.AddPolicy(MonitorPolicies.Settings, policy => policy.RequireAssertion(_ => true));
    options.AddPolicy(MonitorPolicies.Discord, policy => policy.RequireAssertion(_ => true));
});
builder.Services.AddSingleton<AuthenticationStateProvider, PreviewAuthentication>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapMonitorApi(policy: null);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(MonitorPage).Assembly);

app.Run();

internal sealed class PreviewAuthentication : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity("preview"))));
}
