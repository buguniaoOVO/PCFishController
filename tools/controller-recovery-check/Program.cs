using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
class Checks {
 [STAThread] static void Main() {
  // The constructor reads config relative to THIS harness, never the installed assistant.
  File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"PCFish控制器配置.json"),"{\"Port\":39189,\"AutoBreed\":false,\"HideGameConsole\":false,\"AutoMergePreferHighRarity\":false,\"AutoBreedPreferHighRarity\":true,\"AutoMergeExcludedSeasons\":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20]}");
  File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"PCFish Wiki数据库.json"),"""{"SchemaVersion":1,"Fish":[{"Code":"FS00033","Name":"霜蓝翻车鱼","Tier":3,"Season":1}],"Recipes":[{"Target":"FS00033","Season":1,"Ingredients":[{"Type":"FS00014","Star":1,"Count":1,"Role":"核心鱼"}]}]}""");
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
  Check(!(bool)settingsType.GetProperty("AutoSteamExport").GetValue(reload) &&
        (bool)settingsType.GetProperty("AutoMarketRefresh").GetValue(reload),
        "scheduled quote refresh defaults on while scheduled Steam export defaults off");
  var seasonSettings=(List<int>)settingsType.GetProperty("AutoMergeExcludedSeasons").GetValue(reload);
  Check(seasonSettings.SequenceEqual(new[]{1,2}),"season exclusion migration exposes only S1 and S2");
  Check((string)settingsType.GetProperty("AutoMergePriorityMode").GetValue(reload)=="rarity_low" &&
        (string)settingsType.GetProperty("AutoBreedPriorityMode").GetValue(reload)=="rarity_high",
        "legacy rarity settings migrate to the four-way priority selector");
  var wikiType=asm.GetType("PCFishController.WikiDatabase");
  var wikiData=wikiType.GetProperty("Data",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  var wikiDataType=wikiData.GetType();
  var wikiFish=(IEnumerable)wikiDataType.GetProperty("Fish").GetValue(wikiData);
  var wikiRecipes=(IEnumerable)wikiDataType.GetProperty("Recipes").GetValue(wikiData);
  var s2Fish=wikiFish.Cast<object>().Where(f=>(int)f.GetType().GetProperty("Season").GetValue(f)==2).ToList();
  var s2Recipes=wikiRecipes.Cast<object>().Where(x=>(int)x.GetType().GetProperty("Season").GetValue(x)==2).ToList();
  Check(s2Fish.Count==3 && s2Recipes.Count==3,"legacy S1 wiki cache migrates by adding all three S2 fish and recipes");
  var synthesis=asm.GetType("PCFishController.SynthesisPlanner");
  var s2Route=synthesis.GetMethod("Build",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"FS00038",2});
  var routeType=s2Route.GetType();
  var routeRows=((IEnumerable)routeType.GetProperty("Rows",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(s2Route)).Cast<object>().ToList();
  var hasTwoStarSupport=routeRows.Any(r=>(string)r.GetType().GetProperty("Type").GetValue(r)=="FS00001" && (int)r.GetType().GetProperty("Star").GetValue(r)==2);
  Check((bool)routeType.GetProperty("Exact",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(s2Route) && hasTwoStarSupport,"S2 seasonal route expands required stars and ingredients");
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

  var selectionType=asm.GetType("PCFishController.Selection");
  var pickMerge=selectionType.GetMethod("PickMergeCandidates",BindingFlags.Static|BindingFlags.NonPublic);
  var pickBreed=selectionType.GetMethod("TryPickBreedPair",BindingFlags.Static|BindingFlags.NonPublic);
  object NewFish(string id,string species,int tier,int stars,int breedCount) {
   var f=Activator.CreateInstance(fishType,true);
   fishType.GetProperty("id").SetValue(f,id);fishType.GetProperty("ty").SetValue(f,species);
   fishType.GetProperty("sp").SetValue(f,$"{species}_{tier:D2}_{stars:D2}_01");
   fishType.GetProperty("tier").SetValue(f,tier);fishType.GetProperty("stars").SetValue(f,stars);
   fishType.GetProperty("bc").SetValue(f,breedCount);return f;
  }
  IList FishList(params object[] values) {
   var list=(IList)Activator.CreateInstance(listType);foreach(var f in values)list.Add(f);return list;
  }
  object[] MergeArgs(IList fish,string priority,ISet<int> seasons=null,ISet<string> recipeTypes=null,bool protect=false)
   => new object[]{fish,10,null,protect,1m,priority,seasons??new HashSet<int>(),new HashSet<string>(),recipeTypes??new HashSet<string>()};
  string FirstType(object picks)=>fishType.GetProperty("ty").GetValue(((IEnumerable)picks).Cast<object>().First()).ToString();
  var groupChoices=FishList(
   Enumerable.Range(0,10).Select(i=>NewFish("rh"+i,"FS00030",5,1,0)).ToArray()
    .Concat(Enumerable.Range(0,10).Select(i=>NewFish("sh"+i,"FS00029",4,5,0))).ToArray());
  var rarityHigh=pickMerge.Invoke(null,MergeArgs(groupChoices,"rarity_high"));
  var starsHigh=pickMerge.Invoke(null,MergeArgs(groupChoices,"stars_high"));
  Check(FirstType(rarityHigh)=="FS00030" && FirstType(starsHigh)=="FS00029","merge priority selects rarity or star grade as configured");
  var recipeChoices=FishList(Enumerable.Range(0,10).Select(i=>NewFish("r1"+i,"FS00001",1,1,0)).ToArray()
   .Concat(Enumerable.Range(0,10).Select(i=>NewFish("r2"+i,"FS00002",1,1,0))).ToArray());
  var recipeFiltered=pickMerge.Invoke(null,MergeArgs(recipeChoices,"rarity_high",recipeTypes:new HashSet<string>{"FS00001"}));
  Check(FirstType(recipeFiltered)=="FS00002","merge excludes selected season recipe fish");
  var seasonChoices=FishList(Enumerable.Range(0,10).Select(i=>NewFish("s2"+i,"FS00038",3,1,0)).ToArray()
   .Concat(Enumerable.Range(0,10).Select(i=>NewFish("reg"+i,"FS00019",3,1,0))).ToArray());
  var seasonFiltered=pickMerge.Invoke(null,MergeArgs(seasonChoices,"rarity_high",seasons:new HashSet<int>{2}));
  Check(FirstType(seasonFiltered)=="FS00019","merge excludes selected S2 seasonal fish");
  var unknownQuotes=pickMerge.Invoke(null,MergeArgs(recipeChoices,"rarity_high",protect:true));
  Check(!((IEnumerable)unknownQuotes).Cast<object>().Any(),"market protection keeps fish without matched quotes out of merge");
  var breedChoices=FishList(NewFish("lowstar","FS00001",1,1,1),NewFish("highstar","FS00030",5,5,1));
  var breedArgs=new object[]{breedChoices,0,null,null,new HashSet<string>(),null,"stars_high",null,null};
  var breedOk=(bool)pickBreed.Invoke(null,breedArgs);
  Check(breedOk && fishType.GetProperty("id").GetValue(breedArgs[7]).ToString()=="highstar","breeding star priority selects higher-star parent");
  ((IDisposable)Get("_client")).Dispose();
  ((System.Windows.Forms.Timer)Get("_timer")).Stop();
  ((NotifyIcon)Get("_trayIcon")).Dispose();
  form.Dispose();
  Console.WriteLine("All controller flow checks passed. No game connection was used.");
 }
}
