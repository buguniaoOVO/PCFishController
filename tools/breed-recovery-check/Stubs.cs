using System.Collections.Generic;
namespace Il2CppInterop.Runtime.InteropTypes.Arrays {
 public sealed class Il2CppStringArray { public Il2CppStringArray(string[] items) {} }
}
namespace Il2CppSystem {
 public sealed class Action<A,B,C> {
  public readonly System.Delegate Callback;
  public Action(System.Delegate callback) => Callback = callback;
 }
}
namespace Il2CppInterop.Runtime {
 public static class DelegateSupport {
  public static T ConvertDelegate<T>(System.Delegate callback) => (T)System.Activator.CreateInstance(typeof(T), callback);
 }
}
namespace NN.PF.Core.Network {
 public enum NetworkResult { Success, Fail, NetworkError }
 public sealed class NetworkManager {
#if LEGACY
  public void FishBreed(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray parents, Il2CppSystem.Action<bool,bool,string> cb) => cb.Callback.DynamicInvoke(true,true,"child");
#else
  public void FishBreed(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray parents, Il2CppSystem.Action<NetworkResult,bool,string> cb) => cb.Callback.DynamicInvoke(NetworkResult.Success,true,"child");
#endif
 }
}
namespace NN.PF.UI.Breed {
 public sealed class UIBreed {
  public bool Finished;
#if LEGACY
  public void _Breed_b__999_2(bool result,bool isNew,string id) => Finished = result;
#else
  public void _Breed_b__999_2(NN.PF.Core.Network.NetworkResult result,bool isNew,string id) => Finished = result == NN.PF.Core.Network.NetworkResult.Success;
#endif
 }
}
namespace PCFishAutoHelper {
 internal static class Plugin { public const string PluginVersion="test"; }
 internal static class Journal { public static void Write(string text) {} public static void Error(string text,System.Exception ex) {} }
 internal static class UiState { public static bool HasHugeForecast()=>false; public static string DumpDetail()=>""; public static string DumpWindows()=>""; public static string Summarize()=>""; }
 internal static class WindowGuard {
  public struct Signature { public bool Valid,WindowOpen; public int ActiveNodes; public string RootName; public string Describe()=>""; }
  public static Signature Snapshot()=>default; public static string Compare(Signature a,Signature b)=>null;
 }
 internal static class ServerInventoryCache {
  public static bool Ready=>true;
  public static bool Refresh(System.Action<bool,string> done,out string reason) { reason="";done(true,"{}");return true; }
 }
 internal sealed class FishInfo {
  public string Id,Species,Type,BreedNextDatetime;
  public int Level,Stars,Variant,Grade,Growth,BreedCount,BreedMaxCount,ServerBreedCount;
  public bool IsPlaced,IsLocked;
  public bool CanBreed=>true;
 }
 internal static class GameBridge {
  public static bool ThrowBeforeSend,HasPendingBreed;
  public static int Sends;
  public static System.Action<bool,bool,string,string,string> Complete;
  public struct InteractionState { public bool Busy,InputBlocked,NetworkBusy,WindowOpen; public string Describe()=>""; }
  public static string ActionBusyReason()=>"";
  public static bool StartBreed(IList<string> parents,System.Action<bool,bool,string,string,string> result,out string reason) {
   reason="";
   if(ThrowBeforeSend) throw new System.MissingMethodException("API changed");
   Sends++;HasPendingBreed=true; Complete=(ok,n,id,msg,kind)=>{HasPendingBreed=false;result(ok,n,id,msg,kind);};
   return true;
  }
  public static bool TryGetDataManager(out object data){data=null;return false;}
  public static List<FishInfo> Snapshot(object dm)=>new();
  public static List<string> SnapshotCollectionTypes(object dm)=>new();
  public static InteractionState ReadInteractionState()=>default;
  public static int GetBreedCharge(object dm)=>5;
  public static int GetTankLevel(object dm)=>8;
  public static int GetTankExp(object dm)=>100;
  public static bool CanLevelUp(object dm)=>false;
  public static int EstimateSecondsToNextHeart(object dm)=>0;
  public static bool TryUpgrade(out string detail){detail="";return false;}
  public static bool GetHideBreedingPopup(out string detail){detail="";return false;}
  public static bool SetHideBreedingPopup(bool on,out string detail){detail="";return true;}
 }
}
