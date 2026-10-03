using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
class Checks {
 [STAThread] static void Main() {
  // The constructor reads config relative to THIS harness, never the installed assistant.
  File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"PCFish控制器配置.json"),"{\"Port\":39189,\"AutoBreed\":false}");
  ApplicationConfiguration.Initialize();
  var asm=Assembly.Load("PCFishController");
  var type=asm.GetType("PCFishController.MainForm");
  var form=(Form)Activator.CreateInstance(type,true);
  var flags=BindingFlags.Instance|BindingFlags.NonPublic;
  object Get(string name)=>type.GetField(name,flags).GetValue(form);
  void Set(string name,object value)=>type.GetField(name,flags).SetValue(form,value);
  void Call(string name,params object[] args)=>type.GetMethod(name,flags).Invoke(form,args);
  void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
  var tracker=Get("_breedRequest");
  var trackerType=tracker.GetType();
  void Track(string name,params object[] args)=>trackerType.GetMethod(name,flags).Invoke(tracker,args);
  bool Active()=>(bool)trackerType.GetProperty("Active",flags).GetValue(tracker);
  var closeCombo=(ComboBox)Get("_closeBehaviorCombo");
  closeCombo.SelectedIndex=2;
  Call("ChangeLanguage","en");
  Check(closeCombo.SelectedIndex==2 && closeCombo.Items[2].ToString()=="Minimize to System Tray","language switch preserves the selected close action");
  var settingsType=asm.GetType("PCFishController.AppSettings");
  var reload=settingsType.GetMethod("Load",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
  Check((string)settingsType.GetProperty("CloseBehavior").GetValue(reload)=="tray","close action persists across settings reload");
  closeCombo.SelectedIndex=0;
  Call("ChangeLanguage","zh");
  var fishType=asm.GetType("PCFishController.FishDto");
  var listType=typeof(List<>).MakeGenericType(fishType);
  string before="""[{"id":"a","ty":"FS00001","sp":"FS00001_01_01_01","bc":2,"bm":2,"tier":1,"stars":1},{"id":"b","ty":"FS00002","sp":"FS00002_01_01_01","bc":2,"bm":2,"tier":1,"stars":1}]""";
  var snapshot=JsonSerializer.Deserialize(before,listType);
  void Begin(string id) {
   Track("Begin",id);Set("_awaitingBreed",true);Set("_roundActive",true);
   Set("_preBreedSnapshot",snapshot);Set("_pendingParentA","a");Set("_pendingParentB","b");
   Set("_preBreedCharge",2);Set("_breedRequestedAt",DateTime.Now.AddSeconds(-40));
  }
  void Line(object msg)=>Call("ProcessLine",JsonSerializer.Serialize(msg));
  Begin("late");
  Call("OnTick",null,EventArgs.Empty);
  Check((bool)Get("_awaitingBreed") && ReferenceEquals(Get("_preBreedSnapshot"),snapshot),"controller watchdog keeps request and parent snapshot");
  Line(new {type="state",ok=true,charge=2,fish=JsonSerializer.Deserialize<object>(before)});
  Check(Active() && Get("_preBreedSnapshot")!=null,"state reads during timeout do not count as a failed breeding");
  Line(new {type="result",cmd="breed",requestId="late",terminal=false,ok=false,errorKind="uncertain",msg="pending"});
  Line(new {type="result",cmd="breed",requestId="late",terminal=true,ok=true,id="child",msg="late success"});
  Check(!(bool)Get("_awaitingBreed") && (bool)Get("_recoverAfterBreedVerification"),"late terminal result schedules inventory verification");
  var after=before.Replace("\"bc\":2","\"bc\":1").TrimEnd(']')+",{\"id\":\"child\",\"ty\":\"FS00003\",\"sp\":\"FS00003_01_01_01\",\"tier\":1,\"stars\":1,\"bc\":2}]";
  Line(new {type="state",ok=true,charge=1,fish=JsonSerializer.Deserialize<object>(after)});
  Check(!Active() && (string)Get("_actionPauseReason")=="","verified late success clears unknown pause");
  var successes=(int)Get("_roundSuccesses");
  Line(new {type="result",cmd="breed",requestId="late",terminal=true,ok=true,id="child"});
  Check((int)Get("_roundSuccesses")==successes,"duplicate final reply does not count again");
  Begin("fault");
  Line(new {type="result",cmd="breed",requestId="fault",terminal=true,ok=false,errorKind="compatibility",msg="missing method"});
  Check(!Active() && (DateTime)Get("_autoResumeAt")==DateTime.MinValue,"incompatible game API reports a concrete final error");
  Begin("read-retry");
  Line(new {type="result",cmd="breed",requestId="read-retry",terminal=true,ok=true,id="child",msg="done"});
  Line(new {type="state",ok=false,msg="read failed"});
  Check(Active() && (DateTime)Get("_breedSettleAt")>DateTime.Now,"failed state read schedules another read while retaining final result");
  ((IDisposable)Get("_client")).Dispose();
  ((System.Windows.Forms.Timer)Get("_timer")).Stop();
  ((NotifyIcon)Get("_trayIcon")).Dispose();
  form.Dispose();
  Console.WriteLine("All controller flow checks passed. No game connection was used.");
 }
}
