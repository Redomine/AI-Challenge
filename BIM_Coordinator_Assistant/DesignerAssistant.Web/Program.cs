using DesignerAssistant.Web.Components;
using DesignerAssistant.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient();
builder.Services.AddScoped<AssistantSession>();
builder.Services.AddSingleton<ScheduledTaskService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ScheduledTaskService>());

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
