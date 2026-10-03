using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using PCFishAutoHelper;
using PCFishController;
static void Check(bool test,string name) { if(!test)throw new Exception(name);Console.WriteLine("PASS "+name); }
var type=typeof(BridgeServer);
var flags=BindingFlags.NonPublic|BindingFlags.Static;
void Set(string field,object value)=>type.GetField(field,flags).SetValue(null,value);
var clientType=type.GetNestedType("Client",BindingFlags.NonPublic);
var requestType=type.GetNestedType("Request",BindingFlags.NonPublic);
object MakeRequest(string command, params string[] args) {
 var client=Activator.CreateInstance(clientType,true);
 var request=Activator.CreateInstance(requestType,true);
 requestType.GetField("Client",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(request,client);
 requestType.GetField("Command",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(request,command);
 requestType.GetField("Args",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(request,args);
 var inbox=type.GetField("Inbox",flags).GetValue(null);
 inbox.GetType().GetMethod("Enqueue").Invoke(inbox,new[]{request});
 return client;
}
JsonElement Response(object client) {
 var q=(ConcurrentQueue<string>)clientType.GetField("Out",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(client);
 if(!q.TryDequeue(out var text))throw new Exception("no response");
 return JsonDocument.Parse(text).RootElement.Clone();
}
BridgeServer.Configure(true,60,0);
Set("_armed",true);
GameBridge.ThrowBeforeSend=true;
var bad=MakeRequest("BREED","fault","a","b");
BridgeServer.Pump();
var error=Response(bad);
Check(error.GetProperty("terminal").GetBoolean() && error.GetProperty("errorKind").GetString()=="compatibility" && GameBridge.Sends==0,"missing method returns a final error before send");
GameBridge.ThrowBeforeSend=false;
Set("_armed",true);
var request=MakeRequest("BREED","operation-a","a","b");
BridgeServer.Pump();
Set("_pendingBreedAt",DateTime.UtcNow.AddSeconds(-30));
BridgeServer.Pump();
var waiting=Response(request);
Check(!waiting.GetProperty("terminal").GetBoolean() && GameBridge.Sends==1,"soft timeout retains one pending request");
var tracker=new BreedRequestTracker();
tracker.Begin("operation-a");
Check(tracker.Accept("operation-a",false) && tracker.Waiting && tracker.TimedOut,"controller retains timed-out request");
var poll=MakeRequest("BREEDSTATUS","operation-a");
BridgeServer.Pump();
Check(!Response(poll).GetProperty("terminal").GetBoolean() && GameBridge.Sends==1,"status polling sends no breeding request");
GameBridge.Complete(true,true,"child","done","");
var final=Response(request);
Check(final.GetProperty("ok").GetBoolean() && tracker.Accept(final.GetProperty("requestId").GetString(),true) && !tracker.Waiting,"late success is accepted after timeout");
Check(!tracker.Accept("operation-a",true),"duplicate terminal callback is ignored");
var reconnected=MakeRequest("BREEDSTATUS","operation-a");
BridgeServer.Pump();
Check(Response(reconnected).GetProperty("ok").GetBoolean(),"final result replays to a reconnected client");
var duplicate=MakeRequest("BREED","operation-a","a","b");
BridgeServer.Pump();
Check(Response(duplicate).GetProperty("ok").GetBoolean() && GameBridge.Sends==1,"duplicate request id cannot breed twice");
tracker.Begin("operation-b");
Check(!tracker.Accept("operation-a",true) && tracker.Waiting,"old result cannot complete the next request");
var unknown=MakeRequest("BREEDSTATUS","unrecognized");
BridgeServer.Pump();
Check(!Response(unknown).GetProperty("terminal").GetBoolean(),"missing history remains unresolved");
Check(NativeBreedingApi.Validate(out var detail),detail);
var ui=new NN.PF.UI.Breed.UIBreed();
var callback=NativeBreedingApi.CreateCallback((result,isNew,id)=>{
 Check(NativeBreedingApi.IsSuccess(result) && id=="child","actual callback type is converted");
 NativeBreedingApi.Finish(ui,result,isNew,id);
});
NativeBreedingApi.Send(new NN.PF.Core.Network.NetworkManager(),new[]{"a","b"},callback);
Check(ui.Finished,"native completion is found by signature");
Console.WriteLine("All recovery checks passed.");

