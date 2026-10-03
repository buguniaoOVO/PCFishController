using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NN.PF.Core.Network;
using NN.PF.UI.Breed;

namespace PCFishAutoHelper;

/// <summary>按签名定位原生繁育入口，兼容 bool 和 NetworkResult 回调。</summary>
internal static class NativeBreedingApi
{
    private static MethodInfo _send, _finish;
    private static Type _resultType, _callbackType;
    internal static string Detail { get; private set; } = "";
    private static readonly BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static bool Validate(out string detail)
    {
        try
        {
            _send = typeof(NetworkManager).GetMethods(Methods).Single(m =>
                m.Name == "FishBreed" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(Il2CppStringArray));
            _callbackType = _send.GetParameters()[1].ParameterType;
            var args = _callbackType.GetGenericArguments();
            if (args.Length != 3 || args[1] != typeof(bool) || args[2] != typeof(string))
                throw new MissingMethodException("繁育回调签名不受支持");
            _resultType = args[0];
            if (_resultType != typeof(bool) && (!_resultType.IsEnum || !Enum.GetNames(_resultType).Contains("Success")))
                throw new MissingMethodException("繁育结果类型不受支持");
            _finish = typeof(UIBreed).GetMethods(Methods).Single(m =>
                m.Name.StartsWith("_Breed_b__", StringComparison.Ordinal) && m.ReturnType == typeof(void) &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args));
            detail = "原生繁育接口已匹配：" + _resultType.Name + " / " + _finish.Name;
            Detail = detail;
            return true;
        }
        catch (Exception ex)
        {
            _send = _finish = null;
            detail = "游戏繁育接口不兼容，请更新助手：" + ex.GetBaseException().Message;
            Detail = detail;
            return false;
        }
    }

    internal static object CreateCallback(Action<object, bool, string> callback)
    {
        if (_send == null) throw new MissingMethodException("原生繁育接口未通过检查");
        var result = Expression.Parameter(_resultType, "result");
        var isNew = Expression.Parameter(typeof(bool), "isNew");
        var id = Expression.Parameter(typeof(string), "id");
        var body = Expression.Invoke(Expression.Constant(callback), Expression.Convert(result, typeof(object)), isNew, id);
        var managedType = typeof(Action<,,>).MakeGenericType(_resultType, typeof(bool), typeof(string));
        var managed = Expression.Lambda(managedType, body, result, isNew, id).Compile();
        var convert = typeof(DelegateSupport).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        return convert.MakeGenericMethod(_callbackType).Invoke(null, new object[] { managed });
    }

    internal static bool IsSuccess(object result) => result is bool ok ? ok : result?.ToString() == "Success";
    internal static void Send(NetworkManager net, string[] parents, object callback)
        => _send.Invoke(net, new object[] { new Il2CppStringArray(parents), callback });
    internal static void Finish(UIBreed ui, object result, bool isNew, string id)
        => _finish.Invoke(ui, new[] { result, (object)isNew, id });
}
