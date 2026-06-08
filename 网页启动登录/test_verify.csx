// Quick checks for SapWebLauncher URI parsing and template direction.
// The canonical self-test is:
//   SapWebLauncher.exe test

using System.Collections.Specialized;
using System.Text.Json;

string uri = "sap-rpa://run?action=run&tcode=ZFI019NL&system=Y4Q&client=630&user=MYUSER&pw=MYPASS&lang=ZH&sysnr=00";
var query = ParseUri(uri);
Console.WriteLine($"action = {query["action"]}");
Console.WriteLine($"tcode  = {query["tcode"]}");
Console.WriteLine($"system = {query["system"]}");
Console.WriteLine($"client = {query["client"]}");
Console.WriteLine($"user   = {query["user"]}");
Console.WriteLine($"pw     = {query["pw"]}");
Console.WriteLine($"lang   = {query["lang"]}");
Console.WriteLine($"sysnr  = {query["sysnr"]}");

bool pass = query["action"] == "run"
         && query["tcode"] == "ZFI019NL"
         && query["system"] == "Y4Q"
         && query["client"] == "630"
         && query["user"] == "MYUSER"
         && query["pw"] == "MYPASS"
         && query["lang"] == "ZH"
         && query["sysnr"] == "00";

Console.WriteLine($"[sap-rpa URI] {(pass ? "PASS" : "FAIL")}");

string payloadUri = "sap-rpa://run?payload=%7B%22tCode%22%3A%22ZFI019NL%22%2C%22plant%22%3A%221024%22%7D";
var payloadQuery = ParseUri(payloadUri);
MergePayload(payloadQuery);
Console.WriteLine($"[payload] {(payloadQuery["tcode"] == "ZFI019NL" && payloadQuery["plant"] == "1024" ? "PASS" : "FAIL")}");

static NameValueCollection ParseUri(string raw)
{
    var result = new NameValueCollection();
    int schemePos = raw.IndexOf("://", StringComparison.Ordinal);
    string rest = schemePos >= 0 ? raw[(schemePos + 3)..] : raw;

    int queryPos = rest.IndexOf('?');
    if (queryPos >= 0)
    {
        string path = rest[..queryPos].Trim('/');
        if (!string.IsNullOrWhiteSpace(path) && !path.Contains('='))
            result["action"] = path;

        ParseQueryPart(rest[(queryPos + 1)..], result);
    }
    else
    {
        ParseQueryPart(rest.Trim('/'), result);
    }

    return result;
}

static void ParseQueryPart(string queryPart, NameValueCollection result)
{
    var parts = queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries);
    foreach (var part in parts)
    {
        int eq = part.IndexOf('=');
        if (eq < 0) continue;

        string key = Uri.UnescapeDataString(part[..eq]).Trim().ToLowerInvariant();
        string val = Uri.UnescapeDataString(part[(eq + 1)..]).Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(key))
            result[key] = val;
    }
}

static void MergePayload(NameValueCollection query)
{
    string? payload = query["payload"];
    if (string.IsNullOrWhiteSpace(payload))
        return;

    using var doc = JsonDocument.Parse(payload);
    foreach (var prop in doc.RootElement.EnumerateObject())
    {
        string key = prop.Name.ToLowerInvariant();
        if (query[key] == null)
            query[key] = prop.Value.GetString();
    }
}
