using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using LenovoLegionToolkit.Lib;

internal static class UpdateTransportChecks
{
    public static void Run(Action<bool, string> check)
    {
        var factory = new HttpClientFactory();
        var proxy = new Uri("http://127.0.0.1:9");
        factory.SetProxy(proxy, "offline-fixture", "offline-fixture", true);
        using var ordinaryHandler = factory.CreateHandler();
        using var client = factory.CreateForUpdates();
        var handlerField = typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
        var updateHandler = (HttpClientHandler)handlerField.GetValue(client)!;
        check(ordinaryHandler.ServerCertificateCustomValidationCallback is not null &&
            updateHandler.ServerCertificateCustomValidationCallback is null &&
            updateHandler.Proxy is WebProxy configured && configured.Address == proxy,
            "update HTTPS preserves the configured proxy while refusing the optional certificate-validation bypass; no network request");
    }
}
