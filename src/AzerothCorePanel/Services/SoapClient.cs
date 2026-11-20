using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AzerothCorePanel.Services;

/// <summary>SOAP settings read from worldserver.conf.</summary>
public sealed record SoapConfig(bool Enabled, string Host, int Port, string ConfPath)
{
    public string Endpoint => $"{Host}:{Port}";
}

/// <summary>Output of a command; Success is false when worldserver reported the command as failed.</summary>
public sealed record SoapResult(bool Success, string Output);

/// <summary>
/// Runs GM commands through worldserver's SOAP service (ACSoap): an HTTP POST with Basic authentication
/// and an "executeCommand" envelope in the "urn:AC" namespace. The account needs GM level 3 (administrator).
/// </summary>
public static class SoapClient
{
    private const int DefaultPort = 7878;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static SoapConfig? ReadConfig(string worldserverExe)
    {
        string? confPath = ConfFile.Find(worldserverExe, "worldserver.conf");
        if (confPath is null)
        {
            return null;
        }

        Dictionary<string, string> values = ConfFile.Read(confPath);
        bool enabled = values.TryGetValue("SOAP.Enabled", out string? flag) && flag.Trim() == "1";

        string host = values.TryGetValue("SOAP.IP", out string? ip) && ip.Length > 0 ? ip : "127.0.0.1";
        if (host is "0.0.0.0" or "::")
        {
            // Bound to every interface: connect through the local one.
            host = "127.0.0.1";
        }

        int port = values.TryGetValue("SOAP.Port", out string? portText) && int.TryParse(portText, out int p)
            ? p
            : DefaultPort;

        return new SoapConfig(enabled, host, port, confPath);
    }

    /// <summary>
    /// Turns SOAP on in worldserver.conf by changing only the "SOAP.Enabled = 0" line (worldserver must restart).
    /// Returns false when the line was not found.
    /// </summary>
    public static bool EnableInConf(string confPath)
    {
        byte[] bytes = File.ReadAllBytes(confPath);
        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var encoding = new UTF8Encoding(hasBom);
        string text = encoding.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var regex = new Regex(@"^(\s*SOAP\.Enabled\s*=\s*)0(\s*)$", RegexOptions.Multiline);
        if (!regex.IsMatch(text))
        {
            return false;
        }

        string updated = regex.Replace(text, "${1}1${2}", 1);
        File.WriteAllText(confPath, updated, encoding);
        return true;
    }

    public static async Task<SoapResult> ExecuteAsync(
        SoapConfig config, string user, string password, string command, CancellationToken token)
    {
        string envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://schemas.xmlsoap.org/soap/envelope/\"" +
            " xmlns:SOAP-ENC=\"http://schemas.xmlsoap.org/soap/encoding/\"" +
            " xmlns:xsi=\"http://www.w3.org/1999/XMLSchema-instance\"" +
            " xmlns:xsd=\"http://www.w3.org/1999/XMLSchema\" xmlns:ns1=\"urn:AC\">" +
            "<SOAP-ENV:Body><ns1:executeCommand><command>" + SecurityElement.Escape(command) +
            "</command></ns1:executeCommand></SOAP-ENV:Body></SOAP-ENV:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{config.Endpoint}/")
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, token);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Não foi possível conectar ao SOAP em {config.Endpoint}. O worldserver está online e com " +
                "SOAP.Enabled = 1?", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new InvalidOperationException("Conta ou senha inválida.");
                case HttpStatusCode.Forbidden:
                    throw new InvalidOperationException("A conta precisa ser GM nível 3 (administrador) para usar o SOAP.");
            }

            string body = await response.Content.ReadAsStringAsync(token);
            XDocument document;
            try
            {
                document = XDocument.Parse(body);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Resposta inesperada do SOAP (HTTP {(int)response.StatusCode}).", ex);
            }

            string? result = FindElement(document, "result");
            if (result is not null)
            {
                return new SoapResult(true, Clean(result));
            }

            // A failed command comes back as a SOAP fault whose text is the command output.
            string? fault = FindElement(document, "faultstring") ?? FindElement(document, "detail");
            if (fault is not null)
            {
                return new SoapResult(false, Clean(fault));
            }

            // A command with no output (for example "saveall" prints little or nothing).
            return new SoapResult(response.IsSuccessStatusCode, string.Empty);
        }
    }

    private static string? FindElement(XDocument document, string localName) =>
        document.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string Clean(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
