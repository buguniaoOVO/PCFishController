using System;
using HarmonyLib;

namespace PCFishAutoHelper;

/// <summary>旁路读取本笔繁育的错误证据，保留游戏自己的请求和回包处理。</summary>
internal static class NetworkDiagnostics
{
    [HarmonyPatch(typeof(NN.PF.Core.Network.NetworkManager.__c__DisplayClass21_0),
        nameof(NN.PF.Core.Network.NetworkManager.__c__DisplayClass21_0._FishBreed_b__0))]
    private static class ServerResponse
    {
        private static void Prefix(string __0) => GameBridge.ObserveServerResponse(__0);
    }

    [HarmonyPatch(typeof(NN.PF.Core.Network.NetworkManager.__c__DisplayClass21_0),
        nameof(NN.PF.Core.Network.NetworkManager.__c__DisplayClass21_0._FishBreed_b__1))]
    private static class TransportFailure
    {
        private static void Prefix() => GameBridge.ObserveTransportFailure();
    }

    [HarmonyPatch(typeof(NANOOGame.Network.NetworkManager._Put_d__25),
        nameof(NANOOGame.Network.NetworkManager._Put_d__25.MoveNext))]
    private static class TransportResult
    {
        private static void Prefix(NANOOGame.Network.NetworkManager._Put_d__25 __instance)
        {
            if (!GameBridge.HasPendingBreed || __instance.__1__state != 1) return;
            try
            {
                var request = __instance._www_5__2;
                if (request == null || !Uri.TryCreate(request.url, UriKind.Absolute, out var uri) ||
                    !uri.AbsolutePath.Contains("breed", StringComparison.OrdinalIgnoreCase)) return;
                GameBridge.ObserveTransport(request.responseCode, request.result.ToString(), request.error);
            }
            catch (Exception ex) { Journal.Error("读取繁育 HTTP 诊断失败", ex); }
        }
    }

    [HarmonyPatch(typeof(NN.PF.UI.Alter.UIAlterMessage), nameof(NN.PF.UI.Alter.UIAlterMessage.Show))]
    private static class GameErrorMessage
    {
        private static void Prefix(string __0) => GameBridge.ObserveGameMessage(__0);
    }
}
