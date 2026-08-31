using System.Net.Http.Headers;

namespace Zashboard.Infrastructure.Clash.Transport;

internal static class ClashAuthentication
{
    public static void ApplyBearer(HttpRequestHeaders headers, string secret)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Contains('\r', StringComparison.Ordinal) ||
            secret.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A backend credential must not contain newline characters.",
                nameof(secret));
        }

        _ = headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
    }
}
