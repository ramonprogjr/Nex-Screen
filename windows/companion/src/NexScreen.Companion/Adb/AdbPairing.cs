using NexScreen.Companion.Util;

namespace NexScreen.Companion.Adb;

public sealed record PairingResult(bool Ok, string Message);

/// <summary>Input checks for the "Parear tablet por Wi-Fi" dialog.</summary>
public static class PairingInput
{
    /// <summary>The Android pairing code: exactly 6 ASCII digits.</summary>
    public static bool IsValidCode(string? code) => code is { Length: 6 } && code.All(char.IsAsciiDigit);

    /// <summary>Optional "IP:porta": empty means discover over mDNS (<paramref name="endpoint"/> null).</summary>
    public static bool TryParseEndpoint(string? text, out string? endpoint)
    {
        endpoint = null;
        var t = text?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            return true;
        }

        if (!AdbOutput.IsNetworkAddress(t))
        {
            return false;
        }

        endpoint = t;
        return true;
    }
}

/// <summary>
/// `adb pair` driven by the tray dialog: finds the tablet's pairing service over mDNS when no address is given, then
/// pairs with the code passed on stdin (never on the command line, never logged). Blocking; call off the UI thread.
/// </summary>
public static class AdbPairing
{
    private static readonly TimeSpan MdnsTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(20);
    private const int DiscoveryAttempts = 4;

    public static PairingResult Pair(string? explicitAdbPath, string? endpoint, string code, Action<string>? progress = null)
    {
        if (!PairingInput.IsValidCode(code))
        {
            return new PairingResult(false, "O código deve ter exatamente 6 dígitos.");
        }

        var adb = AdbLocator.Find(explicitAdbPath);
        if (adb is null)
        {
            return new PairingResult(false, "adb não encontrado. Instale com: winget install --id Google.PlatformTools -e");
        }

        if (endpoint is null)
        {
            progress?.Invoke("Procurando o tablet na rede…");
            endpoint = Discover(adb);
            if (endpoint is null)
            {
                Log.Info("Wireless pairing: no pairing service found over mDNS.");
                return new PairingResult(false,
                    "Nenhum tablet em modo de pareamento foi encontrado na rede. Deixe a tela \"Parear dispositivo com " +
                    "código de pareamento\" aberta no tablet, confira se os dois estão na mesma rede Wi-Fi, ou digite o IP:porta.");
            }
        }

        progress?.Invoke($"Pareando com {endpoint}…");
        var r = AdbRunner.RunWithInput(adb, PairTimeout, code + "\n", "pair", endpoint);
        var result = Interpret(r, code);
        Log.Info(result.Ok
            ? $"Wireless pairing with {AdbOutput.MaskSerial(endpoint)} succeeded."
            : $"Wireless pairing with {AdbOutput.MaskSerial(endpoint)} failed ({(r.TimedOut ? "timeout" : Detail(Mask(r.Output, code)))}).");
        return result;
    }

    /// <summary>Turns the `adb pair` result into the dialog message; the code never appears in it.</summary>
    internal static PairingResult Interpret(AdbResult r, string code)
    {
        if (r.TimedOut)
        {
            return new PairingResult(false,
                $"O tablet não respondeu em {PairTimeout.TotalSeconds:0} s. Confira o IP:porta e se a tela de pareamento continua aberta.");
        }

        var output = Mask(r.Output, code);
        if (AdbOutput.PairSucceeded(output))
        {
            return new PairingResult(true, "Tablet pareado. Conectando por Wi-Fi…");
        }

        return new PairingResult(false, output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            ? "Código incorreto ou a conexão caiu. Confira o código no tablet e tente de novo."
            : $"Não foi possível parear: {Detail(output)}");
    }

    private static string Mask(string output, string code) =>
        code.Length == 0 ? output : output.Replace(code, "******", StringComparison.Ordinal);

    /// <summary>The pairing service shows up in `adb mdns services` a moment after the tablet opens the pairing screen.</summary>
    private static string? Discover(string adb)
    {
        for (var i = 0; i < DiscoveryAttempts; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1.5));
            }

            var r = AdbRunner.Run(adb, MdnsTimeout, "mdns", "services");
            var services = r.Ok ? AdbOutput.ParseTlsPairingServices(r.Output) : [];
            if (services.Count > 0)
            {
                if (services.Count > 1)
                {
                    Log.Info($"Wireless pairing: {services.Count} pairing services found; using the first.");
                }

                return services[0];
            }
        }

        return null;
    }

    private static string Detail(string output)
    {
        var line = output.Split('\n')
            .Select(l => l.Replace("Enter pairing code:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('*')) ?? "sem resposta do adb";
        return line.Length > 160 ? line[..160] : line;
    }
}
