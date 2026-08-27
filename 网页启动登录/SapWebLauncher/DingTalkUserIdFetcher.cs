using SAP.Middleware.Connector;
using System;

namespace SapWebLauncher;

internal sealed class DingTalkUserIdFetchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Pernr { get; init; } = "";
    public string Ddid { get; init; } = "";
}

// Resolve the webpage personnel number to the real DingTalk userid in SAP.
internal sealed class DingTalkUserIdFetcher
{
    internal const string FunctionName = "ZFI_GET_DDID";
    internal const string PersonnelNumberParameter = "IV_PERNR";
    internal const string DingTalkUserIdParameter = "OV_DDID";

    public DingTalkUserIdFetchResult Fetch(SapNcoConnectionConfig connectionConfig, string pernr)
    {
        string normalizedPernr = (pernr ?? "").Trim();
        if (normalizedPernr.Length == 0)
        {
            return new DingTalkUserIdFetchResult
            {
                Message = $"{FunctionName} requires {PersonnelNumberParameter}."
            };
        }

        string configError = "connection configuration is missing";
        if (connectionConfig == null || !connectionConfig.IsComplete(out configError))
        {
            return new DingTalkUserIdFetchResult
            {
                Pernr = normalizedPernr,
                Message = $"{FunctionName} cannot start: {configError}"
            };
        }

        try
        {
            var destination = SapRpaNcoDestinationProvider.GetDestination(connectionConfig);
            var function = destination.Repository.CreateFunction(FunctionName);
            function.SetValue(PersonnelNumberParameter, normalizedPernr);
            function.Invoke(destination);

            string ddid = (function.GetString(DingTalkUserIdParameter) ?? "").Trim();
            if (ddid.Length == 0)
            {
                return new DingTalkUserIdFetchResult
                {
                    Pernr = normalizedPernr,
                    Message = $"{FunctionName} returned an empty {DingTalkUserIdParameter}."
                };
            }

            return new DingTalkUserIdFetchResult
            {
                Success = true,
                Pernr = normalizedPernr,
                Ddid = ddid,
                Message = $"{FunctionName} resolved {PersonnelNumberParameter}={normalizedPernr}."
            };
        }
        catch (Exception ex)
        {
            return new DingTalkUserIdFetchResult
            {
                Pernr = normalizedPernr,
                Message = $"{FunctionName} failed for {PersonnelNumberParameter}={normalizedPernr}: {ex.Message}"
            };
        }
    }
}
