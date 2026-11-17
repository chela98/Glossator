using System.ClientModel;
using System.Net.Sockets;

namespace Glossator.AI.Clients;

/// <summary>
/// Sağlayıcı ve taşıma katmanı istisnalarını merkezi olarak sınıflandırır:
/// geçici (408/429/502/503/504/ağ) hatalar, kalıcı yetki/yapılandırma (400/401/403/404/422) hataları
/// ve kullanıcı dostu hata etiketleri tek kaynaktan yönetilir.
/// </summary>
public static class AiExceptionClassifier
{
    public static bool IsFatalConfigurationOrAuth(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            return agg.Flatten().InnerExceptions.Any(IsFatalConfigurationOrAuth);
        }

        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is ClientResultException cre && cre.Status is 400 or 401 or 403 or 404 or 422)
            {
                return true;
            }

            if (current is HttpRequestException hre && hre.StatusCode.HasValue
                && (int)hre.StatusCode.Value is 400 or 401 or 403 or 404 or 422)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsTransient(Exception ex, CancellationToken cancellationToken = default)
    {
        // 1. Çağıranın iptal sinyali kesinlikle geçici hata sayılamaz (Ctrl+C anında durmalı)
        if (cancellationToken.IsCancellationRequested || HasCallerCancellation(ex, cancellationToken))
        {
            return false;
        }

        for (var current = ex; current != null; current = current.InnerException)
        {
            // 2. System.ClientModel (OpenAI SDK) HTTP Durum Kodları
            if (current is ClientResultException cre && cre.Status > 0)
            {
                return IsTransientStatusCode(cre.Status);
            }

            // 3. Standart .NET HttpClient İstisna ve Durum Kodları
            if (current is HttpRequestException hre)
            {
                if (hre.StatusCode.HasValue)
                {
                    return IsTransientStatusCode((int)hre.StatusCode.Value);
                }

                // Durum kodu yoksa soket veya DNS bağlantı kopmasıdır; tekrar dene
                return true;
            }

            // 4. Ağ zaman aşımları (HttpClient TimeoutException sarmalı dahil), soket veya ham I/O akış kopmaları
            if (current is TimeoutException or SocketException || current.GetType() == typeof(IOException))
            {
                return true;
            }
        }

        // Salt iptal (içinde TimeoutException yoksa) yeniden denenmez
        if (ex is OperationCanceledException)
        {
            return false;
        }

        // 5. İkincil Fallback: Durum kodu fırlatmayan özel LLM proxy sunucuları için metin araması
        var message = ex.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("502", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("504", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeFailure(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is ClientResultException cre && cre.Status > 0)
            {
                return FormatHttpStatusLabel(cre.Status);
            }

            if (current is HttpRequestException hre)
            {
                return hre.StatusCode.HasValue
                    ? FormatHttpStatusLabel((int)hre.StatusCode.Value)
                    : "Ağ Bağlantı Kesintisi";
            }

            if (current is TimeoutException)
            {
                return "Ağ Zaman Aşımı";
            }

            if (current is SocketException || current.GetType() == typeof(IOException))
            {
                return "Ağ Bağlantı Kesintisi";
            }
        }

        return "Geçici API Hatası";
    }

    private static string FormatHttpStatusLabel(int statusCode) => statusCode switch
    {
        429 => "Kota Sınırı (HTTP 429)",
        503 => "Sunucu Kapasite/Servis Dolu (HTTP 503)",
        408 or 502 or 504 => $"Geçici Ağ Geçidi/Zaman Aşımı (HTTP {statusCode})",
        _ => $"Geçici Hata (HTTP {statusCode})"
    };

    public static bool IsTransientStatusCode(int statusCode) => statusCode is
        408 // RequestTimeout
        or 429 // TooManyRequests
        or 502 // BadGateway
        or 503 // ServiceUnavailable
        or 504; // GatewayTimeout

    private static bool HasCallerCancellation(Exception ex, CancellationToken callerToken)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException oce
                && oce.CancellationToken.CanBeCanceled
                && oce.CancellationToken == callerToken)
            {
                return true;
            }
        }

        return false;
    }
}
