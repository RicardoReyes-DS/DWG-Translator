using System.Net.Http.Headers;
using DwgTranslator.Agent;
using DwgTranslator.Application;
using DwgTranslator.Infrastructure.Windows;

var parsed = Parse(args);
if (parsed.Error is not null) { Console.Error.WriteLine(parsed.Error); return 2; }
var loaded = AgentConfigurationLoader.Load(parsed.Config!);
if (loaded.Error is not null) { Console.Error.WriteLine(loaded.Error.Code); return 2; }
var configuration = loaded.Configuration!;
var reference = SecretReference.Create(configuration.CredentialReference);
if (!reference.IsSuccess) { Console.Error.WriteLine("AGENT_CREDENTIAL_REFERENCE_INVALID"); return 2; }
var secret = await new WindowsCredentialSecretStore().GetAsync(reference.Value!, CancellationToken.None);
if (!secret.IsSuccess) { Console.Error.WriteLine("AGENT_CREDENTIAL_UNAVAILABLE"); return 3; }

using var client = new HttpClient { BaseAddress = new Uri(configuration.HostUrl), Timeout = TimeSpan.FromSeconds(10) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret.Value);
client.DefaultRequestHeaders.Add("X-Correlation-Id", Guid.NewGuid().ToString("D"));
using var request = new HttpRequestMessage(parsed.Method, parsed.Route);
using var response = await client.SendAsync(request);
Console.Out.WriteLine(await response.Content.ReadAsStringAsync());
return parsed.ExpectMutationDisabled
    ? response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed ? 0 : 1
    : response.IsSuccessStatusCode ? 0 : 1;

static (string? Config, string Route, HttpMethod Method, bool ExpectMutationDisabled, string? Error) Parse(string[] values)
{
    if (values.Length < 3) return (null, "", HttpMethod.Get, false, "AGENT_ARGUMENT_INVALID");
    var command = values[0];
    var route = command switch
    {
        "health" => "v1/health",
        "capabilities" => "v1/capabilities",
        "jobs-list" => "v1/jobs",
        "mutation-probe" => "v1/jobs",
        _ => ""
    };
    string? config = null;
    for (var index = 1; index < values.Length; index++)
    {
        if (values[index] == "--config" && index + 1 < values.Length) config = values[++index];
        else return (config, route, HttpMethod.Get, false, "AGENT_ARGUMENT_INVALID");
    }
    return string.IsNullOrEmpty(route) || string.IsNullOrWhiteSpace(config)
        ? (config, route, HttpMethod.Get, false, "AGENT_ARGUMENT_INVALID")
        : (config, route, command == "mutation-probe" ? HttpMethod.Post : HttpMethod.Get,
            command == "mutation-probe", null);
}
