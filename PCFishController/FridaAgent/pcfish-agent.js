/*
 * PCFish 助手 · Frida 内存代理
 *
 * 运行在 PCFish.exe 进程内，只通过游戏自身的 IL2CPP 对象和原生入口读写数据、
 * 发送合成/繁育请求。不向游戏目录写插件文件，但这段脚本本身加载在游戏进程内。
 *
 * 所有 Unity / IL2CPP 对象调用都排到 GameManager.Update 所在的主线程执行。
 */

const AGENT_VERSION = "1.0.0";
const PROTOCOL_VERSION = "0.39.0";
const MAIN_INTERVAL_SECONDS = 60;

const gameAssembly = Process.getModuleByName("GameAssembly.dll");
function nativeFunction(name, ret, args) {
  return new NativeFunction(gameAssembly.getExportByName(name), ret, args);
}

const il2cpp = {
  domainGet: nativeFunction("il2cpp_domain_get", "pointer", []),
  threadAttach: nativeFunction("il2cpp_thread_attach", "pointer", ["pointer"]),
  assemblyOpen: nativeFunction("il2cpp_domain_assembly_open", "pointer", ["pointer", "pointer"]),
  imageGet: nativeFunction("il2cpp_assembly_get_image", "pointer", ["pointer"]),
  classFromName: nativeFunction("il2cpp_class_from_name", "pointer", ["pointer", "pointer", "pointer"]),
  classGetMethod: nativeFunction("il2cpp_class_get_method_from_name", "pointer", ["pointer", "pointer", "int32"]),
  classGetMethods: nativeFunction("il2cpp_class_get_methods", "pointer", ["pointer", "pointer"]),
  classGetFields: nativeFunction("il2cpp_class_get_fields", "pointer", ["pointer", "pointer"]),
  classGetParent: nativeFunction("il2cpp_class_get_parent", "pointer", ["pointer"]),
  classGetType: nativeFunction("il2cpp_class_get_type", "pointer", ["pointer"]),
  classGetName: nativeFunction("il2cpp_class_get_name", "pointer", ["pointer"]),
  methodGetName: nativeFunction("il2cpp_method_get_name", "pointer", ["pointer"]),
  methodGetParamCount: nativeFunction("il2cpp_method_get_param_count", "uint32", ["pointer"]),
  methodGetParam: nativeFunction("il2cpp_method_get_param", "pointer", ["pointer", "uint32"]),
  methodGetReturnType: nativeFunction("il2cpp_method_get_return_type", "pointer", ["pointer"]),
  fieldGetName: nativeFunction("il2cpp_field_get_name", "pointer", ["pointer"]),
  fieldGetOffset: nativeFunction("il2cpp_field_get_offset", "int32", ["pointer"]),
  fieldGetType: nativeFunction("il2cpp_field_get_type", "pointer", ["pointer"]),
  typeGetName: nativeFunction("il2cpp_type_get_name", "pointer", ["pointer"]),
  typeGetObject: nativeFunction("il2cpp_type_get_object", "pointer", ["pointer"]),
  runtimeInvoke: nativeFunction("il2cpp_runtime_invoke", "pointer", ["pointer", "pointer", "pointer", "pointer"]),
  stringNew: nativeFunction("il2cpp_string_new", "pointer", ["pointer"]),
  arrayLength: nativeFunction("il2cpp_array_length", "uint32", ["pointer"]),
  domainGetAssemblies: nativeFunction("il2cpp_domain_get_assemblies", "pointer", ["pointer", "pointer"]),
  assemblyGetImage: nativeFunction("il2cpp_assembly_get_image", "pointer", ["pointer"]),
  imageGetName: nativeFunction("il2cpp_image_get_name", "pointer", ["pointer"])
};

const domain = il2cpp.domainGet();
il2cpp.threadAttach(domain);

const images = {};
function image(name) {
  if (!images[name]) images[name] = il2cpp.imageGet(il2cpp.assemblyOpen(domain, cstr(name)));
  return images[name];
}

function cstr(value) { return Memory.allocUtf8String(value); }
function readCString(pointer) {
  return pointer && !pointer.isNull() ? pointer.readUtf8String() : null;
}

const classCache = {};
function cls(namespace, name) {
  const key = namespace + "." + name;
  if (!classCache[key]) {
    const ns = namespace ? cstr(namespace) : NULL;
    classCache[key] = il2cpp.classFromName(image("Assembly-CSharp.dll"), ns, cstr(name));
  }
  return classCache[key];
}
function coreClass(namespace, name) {
  const key = "core:" + namespace + "." + name;
  if (!classCache[key]) classCache[key] = il2cpp.classFromName(image("UnityEngine.CoreModule.dll"), cstr(namespace), cstr(name));
  return classCache[key];
}
function tmpClass(namespace, name) {
  const key = "tmp:" + namespace + "." + name;
  if (!classCache[key]) classCache[key] = il2cpp.classFromName(image("Unity.TextMeshPro.dll"), cstr(namespace), cstr(name));
  return classCache[key];
}

// UnityWebRequest 在独立的 UnityWebRequest 模块里；模块名写错时不能把 NULL 传进
// classFromName，否则会让游戏进程崩掉，所以这里自己兜一层。
/// 在「已加载」的程序集里查 image。
/// 绝不调用 il2cpp_domain_assembly_open：它在程序集未加载时会触发加载，
/// 在游戏运行中改动 IL2CPP 状态，会把 GameManager.Update 的钩子弄失效
/// （表现为 pump 不跑、鱼群读不到）。
function findLoadedImage(name) {
  try {
    const size = Memory.alloc(8);
    size.writeU64(0);
    const assemblies = il2cpp.domainGetAssemblies(domain, size);
    if (!assemblies || assemblies.isNull()) return NULL;
    const count = size.readU64().toNumber();
    for (let i = 0; i < count; i++) {
      const assembly = assemblies.add(i * Process.pointerSize).readPointer();
      if (!assembly || assembly.isNull()) continue;
      const image = il2cpp.assemblyGetImage(assembly);
      if (!image || image.isNull()) continue;
      const imageName = il2cpp.imageGetName(image);
      if (imageName && !imageName.isNull() && imageName.readUtf8String() === name) return image;
    }
  } catch (error) { /* 找不到就当没有 */ }
  return NULL;
}

function uwrClass() {
  const key = "uwr";
  if (!(key in classCache)) {
    let found = NULL;
    try {
      const img = findLoadedImage("UnityEngine.UnityWebRequestModule.dll");
      if (img && !img.isNull())
        found = il2cpp.classFromName(img, cstr("UnityEngine.Networking"), cstr("UnityWebRequest"));
    } catch (error) { found = NULL; }
    classCache[key] = found;
  }
  return classCache[key];
}

function findMethodInHierarchy(type, name, argc) {
  let current = type;
  for (let i = 0; i < 12 && current && !current.isNull(); i++) {
    const method = il2cpp.classGetMethod(current, cstr(name), argc);
    if (!method.isNull()) return method;
    current = il2cpp.classGetParent(current);
  }
  return NULL;
}

const methodCache = {};
function method(type, name, argc) {
  const key = type.toString() + "|" + name + "|" + argc;
  if (!(key in methodCache)) methodCache[key] = findMethodInHierarchy(type, name, argc);
  return methodCache[key];
}

const fieldCache = {};
function fieldOffset(type, name) {
  const key = type.toString() + "|" + name;
  if (!(key in fieldCache)) {
    const field = il2cpp.classGetFields ? il2cpp.classGetFields : null;
    let found = NULL;
    const iterator = Memory.alloc(Process.pointerSize);
    iterator.writePointer(NULL);
    let current = type;
    while (current && !current.isNull() && found.isNull()) {
      iterator.writePointer(NULL);
      let item;
      while (!(item = il2cpp.classGetFields(current, iterator)).isNull()) {
        if (readCString(il2cpp.fieldGetName(item)) === name) { found = item; break; }
      }
      if (found.isNull()) current = il2cpp.classGetParent(current);
    }
    fieldCache[key] = found.isNull() ? null : il2cpp.fieldGetOffset(found);
  }
  return fieldCache[key];
}

function readString(pointer) {
  if (!pointer || pointer.isNull()) return null;
  const length = pointer.add(0x10).readInt();
  if (length < 0 || length > 4096) return null;
  return pointer.add(0x14).readUtf16String(length);
}

function invoke(methodInfo, object, args) {
  if (!methodInfo || methodInfo.isNull()) throw new Error("method unavailable");
  let argumentsPointer = NULL;
  if (args && args.length) {
    argumentsPointer = Memory.alloc(Process.pointerSize * args.length);
    for (let i = 0; i < args.length; i++)
      argumentsPointer.add(i * Process.pointerSize).writePointer(args[i] || NULL);
  }
  const exception = Memory.alloc(Process.pointerSize);
  exception.writePointer(NULL);
  const result = il2cpp.runtimeInvoke(methodInfo, object || NULL, argumentsPointer, exception);
  const error = exception.readPointer();
  if (!error.isNull()) throw new Error("managed exception 0x" + error.toString());
  return result;
}
function callVoid(type, object, name, args) { invoke(method(type, name, args ? args.length : 0), object, args); }
function callBool(type, object, name, args) {
  const result = invoke(method(type, name, args ? args.length : 0), object, args);
  return !result.isNull() && result.add(0x10).readU8() !== 0;
}
function callInt(type, object, name, args) {
  const result = invoke(method(type, name, args ? args.length : 0), object, args);
  return result.isNull() ? 0 : result.add(0x10).readInt();
}
function newString(value) { return il2cpp.stringNew(cstr(value)); }

const gameDataClass = () => cls("NN.PF.Core.Managers", "GameDataManager");
const uiMergeClass = () => cls("NN.PF.UI.Merge", "UIMerge");
const uiBreedClass = () => cls("NN.PF.UI.Breed", "UIBreed");
const uiManagerClass = () => cls("NN.PF.Core.Managers", "UIManager");
const confirmClass = () => cls("NN.PF.UI.Confirm", "UIConfirm");
const fishModelClass = () => cls("NN.PF.Models", "FishModel");
// 「发现新鱼！」结果面板。之前用 tmpClass（Unity.TextMeshPro 程序集）取这个类，
// classFromName 永远返回 NULL，Confirm 一次都没真正执行过 —— 结果面板就一直挂在
// 屏幕上锁住输入，界面反复报「游戏输入被提示窗口阻塞 / 等待稳定」。
const fishResultClass = () => cls("NN.PF.UI.Common", "UIFishResult");
const fishResultFxClass = () => cls("NN.PF.UI.Common", "UIFishResultFx");
// UIAlterMessage 的真实命名空间是 NN.PF.UI.Alter（不是 NN.PF.UI.Common），
// 写错路径时 Hide() 同样永远打空。
const alterMessageClass = () => cls("NN.PF.UI.Alter", "UIAlterMessage");
const audioManagerClass = () => cls("NN.PF.Core.Managers", "AudioManager");

// 会话稳定判定阈值。原来写 30 秒，而画面里每次瞬时输入锁都会把计时清零，
// 于是永远「正在等待稳定」。真正需要的是「阻塞已解除且没有网络请求」。
const SESSION_STABLE_MS = 5000;
// 输入锁持续存在多久才算真弹窗（短于这个时长的是按钮动画/音效，不该动手）。
const INPUT_BLOCK_GRACE_MS = 2500;

function singleton(type) {
  const instance = method(type, "get_Instance", 0);
  if (!instance || instance.isNull()) return null;
  try {
    const result = invoke(instance, NULL, null);
    return result && !result.isNull() ? result : null;
  } catch (error) { return null; }
}

function dataManager() { return singleton(gameDataClass()); }

function findObjects(type) {
  const resources = coreClass("UnityEngine", "Resources");
  const find = method(resources, "FindObjectsOfTypeAll", 1);
  if (!find || find.isNull()) return [];
  const typeObject = il2cpp.typeGetObject(il2cpp.classGetType(type));
  const result = invoke(find, NULL, [typeObject]);
  if (!result || result.isNull()) return [];
  const count = il2cpp.arrayLength(result);
  const objects = [];
  for (let i = 0; i < count; i++) {
    const item = result.add(0x20 + i * Process.pointerSize).readPointer();
    if (!item.isNull()) objects.push(item);
  }
  return objects;
}
function findFirst(type) {
  const objects = findObjects(type);
  return objects.length ? objects[0] : null;
}

function gameObjectActive(gameObject) {
  if (!gameObject || gameObject.isNull()) return false;
  try { return callBool(coreClass("UnityEngine", "GameObject"), gameObject, "get_activeInHierarchy", []); }
  catch (error) { return false; }
}

const fishFields = {};
function fishField(type, name) {
  if (!(name in fishFields)) fishFields[name] = fieldOffset(type, name);
  return fishFields[name];
}

function readFishList() {
  const manager = dataManager();
  if (!manager) throw new Error("GameDataManager unavailable");
  const listOffset = fieldOffset(gameDataClass(), "fishList");
  const list = manager.add(listOffset).readPointer();
  if (!list || list.isNull()) return [];
  const count = list.add(0x18).readInt();
  const array = list.add(0x10).readPointer();
  if (!array || array.isNull()) return [];
  const fish = [];
  for (let i = 0; i < count; i++) {
    const item = array.add(0x20 + i * Process.pointerSize).readPointer();
    if (item.isNull()) continue;
    const type = item.readPointer();
    const id = readString(item.add(fishField(type, "Id")).readPointer());
    const species = readString(item.add(fishField(type, "Fish")).readPointer());
    const fishType = readString(item.add(fishField(type, "Type")).readPointer());
    if (!id || !species) continue;
    const parts = species.split("_");
    fish.push({
      id: id,
      sp: species,
      ty: fishType || species.split("_")[0],
      tier: item.add(fishField(type, "Level")).readInt(),
      stars: item.add(fishField(type, "Grade")).readInt(),
      variant: parts.length === 4 ? parseInt(parts[3], 10) : 0,
      growth: item.add(fishField(type, "Growth")).readInt(),
      bc: item.add(fishField(type, "BreedCount")).readInt(),
      bm: item.add(fishField(type, "BreedMaxCount")).readInt(),
      serverBc: -1,
      pl: item.add(fishField(type, "IsPlaced")).readU8() !== 0,
      lk: item.add(fishField(type, "IsLocked")).readU8() !== 0,
      nx: readString(item.add(fishField(type, "BreedNextDatetime")).readPointer())
    });
  }
  return fish;
}

function readCollection() {
  try {
    const manager = dataManager();
    if (!manager) return [];
    const list = manager.add(fieldOffset(gameDataClass(), "fishTypeCollection")).readPointer();
    if (!list || list.isNull()) return [];
    const count = list.add(0x18).readInt();
    const array = list.add(0x10).readPointer();
    const values = [];
    for (let i = 0; i < count; i++) {
      const value = readString(array.add(0x20 + i * Process.pointerSize).readPointer());
      if (value) values.push(value);
    }
    return values;
  } catch (error) { return []; }
}

function readCounters() {
  const manager = dataManager();
  if (!manager) return null;
  const type = gameDataClass();
  return {
    charge: manager.add(fieldOffset(type, "breedCount")).readInt(),
    tankLevel: manager.add(fieldOffset(type, "tankLevel")).readInt(),
    tankExp: manager.add(fieldOffset(type, "tankExp")).readInt(),
    breedTimestamp: manager.add(fieldOffset(type, "breedTimestamp")).readS64(),
    canLevelUp: (function () { try { return callBool(type, manager, "IsCheckTankLevelUp", []); } catch (e) { return false; } })()
  };
}

function secondsToHeart(counter, nowSeconds) {
  if (!counter) return -1;
  if (counter.charge >= 5) return 0;
  if (!counter.breedTimestamp) return -1;
  let elapsed = nowSeconds - counter.breedTimestamp;
  if (elapsed < 0 || elapsed > 86400) return -1;
  return 360 - (elapsed % 360);
}

// ---------------------------------------------------------------- 主线程队列

let updateHooked = false;
let updateHook = null;
let frame = 0;
let armed = false;
let actions = 0;
let lastActionAt = 0;
let pending = null;
let cachedTick = null;
let lastTickFrame = -100;
let frameLimitRequest = null;
let appliedFrameLimit = null;
let lastBlockedNoticeDismissAt = 0;
let lastDismissAttemptAt = 0;
let inputBlockedSince = 0;
let networkSessionStableAt = 0;
const pendingSnapshots = [];

// ---------------------------------------------------------------- 鉴权 header 采集
//
// accessToken / refreshToken 两个托管字段实测恒为空，真 JWT 只经由
// UnityWebRequest.SetRequestHeader 下发（存在 native 侧）。
// 所以这里旁路记录 header，供控制器在进程外直接发 HTTP 用。

let authHook = null;
let authReady = false;
const authHeaders = {};

// 注意：不要在主线程主动调 SeasonInfo 去「逼」出 header。
// runtimeInvoke 一旦阻塞会把整个 pump 卡死，界面就会报「游戏主线程暂未就绪」。
// 鉴权改为被动采集：游戏自己发请求时顺手记下，拿不到就回退游戏内动作路径。

function hookAuthHeaders() {
  if (authHook || authReady) return true;
  try {
    const uwr = uwrClass();
    if (!uwr || uwr.isNull()) return false;
    const setHeader = method(uwr, "SetRequestHeader", 2);
    if (!setHeader || setHeader.isNull()) return false;
    authHook = Interceptor.attach(setHeader.readPointer(), {
      onEnter(args) {
        try {
          const name = readString(args[1]);
          const value = readString(args[2]);
          if (name && value) authHeaders[name] = value;
          // 拿到 Authorization 就够了，剩下交给 pump 主线程摘钩。
          // 这是 Unity 引擎的薄方法，长期 inline hook 有崩进程的风险。
          if (authHeaders["Authorization"]) authReady = true;
        } catch (error) { /* 采集失败不能影响游戏请求 */ }
      }
    });
    return true;
  } catch (error) { return false; }
}

/// 返回当前可用的鉴权 header。
///
/// 只能读缓存：IL2CPP 的任何反射调用都必须排到游戏主线程（见 pump 里挂 hook），
/// 在 RPC 线程直接调 il2cpp_class_from_name 会直接把游戏进程搞崩。
function requestAuthHeaders() {
  const snapshot = {};
  Object.keys(authHeaders).forEach(function (key) { snapshot[key] = authHeaders[key]; });
  return snapshot;
}

function hookMainThread() {
  if (updateHooked) return true;
  try {
    const update = method(cls("NN.PF.Core.Managers", "GameManager"), "Update", 0);
    if (!update || update.isNull()) return false;
    updateHook = Interceptor.attach(update.readPointer(), {
      onEnter() {
        frame++;
        try { pump(); } catch (error) { /* 不让异常打断游戏帧 */ }
      }
    });
    updateHooked = true;
    return true;
  } catch (error) { return false; }
}

function pump() {
  // 鉴权采集的 hook 必须在主线程建立：hookAuthHeaders 里要调 IL2CPP 反射，
  // 在 RPC 线程调会崩游戏。这里每帧判一次，挂上后不再重复。
  // 拿到 Authorization 后在主线程摘钩，把这个薄方法的 hook 窗口压到最短。
  if (authReady && authHook) {
    const finished = authHook;
    authHook = null;
    try { finished.detach(); } catch (error) {}
  }
  serviceSnapshotRequests();
  if (pending && pending.state === "setup") {
    pending.state = "wait";
    try {
      if (pending.kind === "merge") setupMerge(pending);
      else if (pending.kind === "breed") setupBreed(pending);
      else if (pending.kind === "upgrade") return runUpgrade(pending);
      else if (pending.kind === "previewMerge") return runPreviewMerge(pending);
      else if (pending.kind === "tradeexport") setupTradeExport(pending);
    } catch (error) { failPending(error && error.message ? error.message : String(error)); }
    return;
  }
  if (pending && pending.state === "wait") {
    if (frame % 3 === 0) {
      try { checkPending(); } catch (error) { failPending(error && error.message ? error.message : String(error)); }
    }
    return;
  }
  if (frame - lastTickFrame >= 10) {
    lastTickFrame = frame;
    applyFrameLimit();
    try {
      cachedTick = buildTick();
      // 关弹窗不能只在「刚发过动作」的短窗口里做：结果面板是回包后才弹出来的，
      // 那一瞬间读不到，错过之后面板就一直挂着锁输入，界面永远停在
      // 「游戏输入被提示窗口阻塞 / 正在等待稳定」。这里改成常驻巡检，
      // 但要求阻塞已经持续 INPUT_BLOCK_GRACE_MS，避免把按钮动画当弹窗点。
      const held = inputBlockedSince ? Date.now() - inputBlockedSince : 0;
      if (cachedTick.inputBlocked && held >= INPUT_BLOCK_GRACE_MS && Date.now() - lastDismissAttemptAt >= 1200) {
        lastDismissAttemptAt = Date.now();
        let handled = false;
        try { if (closeResultPanelOverlay()) handled = true; } catch (error) {}
        try { if (closeGameConfirm()) handled = true; } catch (error) {}
        try { if (closeBlockingAlterMessage(true)) handled = true; } catch (error) {}
        // 三条认领都没命中：说明是协程被打断后残留的僵尸输入锁，直接清标记。
        if (!handled) clearInputBlockFlag();
      }
    } catch (error) { cachedTick = null; }
  }
}

function applyFrameLimit() {
  if (frameLimitRequest === null || frameLimitRequest === appliedFrameLimit) return;
  try {
    const application = coreClass("UnityEngine", "Application");
    const quality = coreClass("UnityEngine", "QualitySettings");
    callVoid(application, NULL, "set_targetFrameRate", [intValue(frameLimitRequest > 0 ? frameLimitRequest : -1)]);
    callVoid(quality, NULL, "set_vSyncCount", [intValue(0)]);
    appliedFrameLimit = frameLimitRequest;
  } catch (error) { /* 下一帧再试 */ }
}

function intValue(value) {
  const block = Memory.alloc(4);
  block.writeInt(value);
  return block;
}

function sendResult(payload) {
  // 结果框的清理不再挂在这里：动作结束的瞬间面板还没弹出来，几秒窗口容易错过。
  // 现在由 tick 常驻巡检（closeResultPanelOverlay + 僵尸输入锁兜底）。
  send(Object.assign({ type: "result" }, payload));
}
function failPending(message, errorKind) {
  if (!pending) return;
  const current = pending;
  pending = null;
  if (current.ui) { try { callVoid(uiMergeClass(), current.ui, "ResetParent", []); } catch (error) {} }
  sendResult({
    cmd: current.kind,
    ok: false,
    msg: message || "操作失败",
    errorKind: errorKind || "game_state",
    requestId: current.requestId || "",
    terminal: true
  });
}

function closeMergeResultUi(ui) {
  closeResultUi(ui, uiMergeClass());
}

function closeBreedResultUi(ui) {
  closeResultUi(ui, uiBreedClass());
}

/**
 * 收起「发现新鱼！」结果面板：先结束特效动画，再走面板自己的 Confirm。
 * Confirm 就是结果面板上方那个「繁育 / 放置」按钮的回调入口，游戏自己也用它收面板。
 */
function closeResultUi(ui, uiType) {
  // 整个函数必须自吞异常：它会在 checkMerge/checkBreed 里直接调用，
  // 一旦抛出会被上层当成「动作失败」，把一次成功的合成判死。
  try {
    const resultOffset = fieldOffset(uiType, "uiFishResult");
    const fxOffset = fieldOffset(uiType, "uiFishResultFx");
    if (resultOffset === null || resultOffset === undefined) return false;
    const result = ui.add(resultOffset).readPointer();
    const fx = fxOffset === null || fxOffset === undefined ? null : ui.add(fxOffset).readPointer();
    if (fx && !fx.isNull()) { try { callVoid(fishResultFxClass(), fx, "FinishFx", []); } catch (error) {} }
    if (result && !result.isNull()) {
      const resultType = fishResultClass();
      if (!resultType || resultType.isNull()) return false;
      callVoid(resultType, result, "Confirm", []);
      return true;
    }
  } catch (error) { /* 面板已复位时 Confirm 会抛，交给外层兜底 */ }
  return false;
}

/** 结果面板的外层 GameObject 是否真的亮着。 */
function resultPanelVisible(ui, uiType) {
  try {
    const offset = fieldOffset(uiType, "viewResult");
    if (offset === null || offset === undefined) return false;
    const view = ui.add(offset).readPointer();
    return !!view && !view.isNull() && gameObjectActive(view);
  } catch (error) { return false; }
}

/**
 * tick 巡检用：找出还亮着的结果面板并收起。
 * 结果面板是回包之后才弹出来的，动作结束那一瞬间通常还读不到它，
 * 所以不能只在 sendResult 的几秒窗口里关一次。
 */
function closeResultPanelOverlay() {
  const targets = [[uiMergeClass(), "merge"], [uiBreedClass(), "breed"]];
  for (let i = 0; i < targets.length; i++) {
    const type = targets[i][0];
    if (!type || type.isNull()) continue;
    const ui = findFirst(type);
    if (!ui) continue;
    if (!resultPanelVisible(ui, type)) continue;
    closeResultUi(ui, type);
    return true;
  }
  return false;
}

/**
 * 兜底：isBlockUi 由 AudioManager.RunBlockUi 的协程负责复原。协程被打断
 * （切场景、面板重复弹出）时这个标记会永久为 true，输入就再也放不开。
 * 前面三条认领都打空时直接清标记，这一步只改输入锁，不动任何游戏数据。
 */
function clearInputBlockFlag() {
  try {
    const audio = findFirst(audioManagerClass());
    if (!audio) return false;
    const offset = fieldOffset(audioManagerClass(), "isBlockUi");
    if (offset === null || offset === undefined) return false;
    if (audio.add(offset).readU8() === 0) return false;
    audio.add(offset).writeU8(0);
    return true;
  } catch (error) { return false; }
}

/**
 * 关掉游戏自己的通用提示框（UIConfirm）。合成/繁育被服务端拒绝时游戏会弹它，
 * 弹窗期间输入被锁，后续动作会被游戏拒绝。只在弹窗真的可见时点「确定」。
 */
function closeGameConfirm() {
  try {
    const confirm = findFirst(confirmClass());
    if (!confirm) return false;
    const shadow = confirm.add(fieldOffset(confirmClass(), "viewShadow")).readPointer();
    if (!gameObjectActive(shadow)) return false;
    callVoid(confirmClass(), confirm, "Ok", []);
    return true;
  } catch (error) { return false; }
}

/**
 * 某些游戏提示（例如“已重置已添加的鱼”）会让 isBlockUi 持续为 true，
 * 结果自动化一直等待。先处理 UIConfirm；若仍有输入阻塞，再让游戏自己的
 * UIAlterMessage.Hide() 收起提示，不直接改 Unity 对象的 active 状态。
 */
function closeBlockingAlterMessage(inputBlocked) {
  try {
    if (!inputBlocked || Date.now() - lastBlockedNoticeDismissAt < 1500) return false;
    const type = alterMessageClass();
    if (!type || type.isNull()) return false;
    lastBlockedNoticeDismissAt = Date.now();
    const messages = findObjects(type);
    if (messages.length === 0) return false;
    for (let i = 0; i < messages.length; i++) {
      try {
        callVoid(type, messages[i], "Hide", []);
        return true;
      } catch (error) { /* Try the next live UIAlterMessage instance. */ }
    }
  } catch (error) { /* An unavailable notice API must not interrupt a game frame. */ }
  return false;
}

function readResultFish(ui, uiType) {
  try {
    const result = ui.add(fieldOffset(uiType, "uiFishResult")).readPointer();
    if (!result || result.isNull()) return null;
    const fish = result.add(fieldOffset(fishResultClass(), "fish")).readPointer();
    if (!fish || fish.isNull()) return null;
    const type = fish.readPointer();
    return {
      id: readString(fish.add(fishField(type, "Id")).readPointer()),
      sp: readString(fish.add(fishField(type, "Fish")).readPointer())
    };
  } catch (error) { return null; }
}

function mergeSlotCount(ui) {
  const list = ui.add(fieldOffset(uiMergeClass(), "mergeFishList")).readPointer();
  if (!list || list.isNull()) return 0;
  const length = il2cpp.arrayLength(list);
  let filled = 0;
  for (let i = 0; i < length; i++) {
    const value = list.add(0x20 + i * Process.pointerSize).readPointer();
    if (value && !value.isNull() && readString(value)) filled++;
  }
  return filled;
}

function setupMerge(state) {
  const ui = findFirst(uiMergeClass());
  if (!ui) throw new Error("找不到游戏的合成对象");
  const type = uiMergeClass();
  state.ui = ui;
  callVoid(type, ui, "ResetParent", []);
  for (let i = 0; i < state.ids.length; i++) {
    callBool(type, ui, "SetParent", [newString(state.ids[i])]);
  }
  const realFilled = mergeSlotCount(ui);
  if (realFilled < 10) throw new Error("游戏合成槽只接受了 " + realFilled + "/10 条材料");
  state.before = {};
  readFishList().forEach(function (fish) { state.before[fish.id] = fish; });
  callVoid(type, ui, "Merge", []);
  state.deadline = Date.now() + 45000;
}

function checkPending() {
  const state = pending;
  if (!state || state.state !== "wait") return;
  if (Date.now() > state.deadline) {
    failPending(state.kind === "merge" ? "合成回包超时，已释放动作锁" : "繁育回包超时", "timeout");
    return;
  }
  if (state.kind === "merge") checkMerge(state);
  else if (state.kind === "breed") checkBreed(state);
  else if (state.kind === "tradeexport") checkTradeExport(state);
}

function setupTradeExport(state) {
  if (!state.ids || state.ids.length === 0) throw new Error("没有选择要导出的鱼");
  const ui = findFirst(cls("NN.PF.UI.Trade", "UITrade"));
  if (!ui) throw new Error("游戏交易对象尚未加载");
  const beforeFish = readFishList();
  const byId = {};
  beforeFish.forEach(function (fish) { byId[fish.id] = fish; });
  for (let i = 0; i < state.ids.length; i++) {
    const fish = byId[state.ids[i]];
    if (!fish || fish.lk || fish.pl) throw new Error("所选鱼已不存在、锁定或展示中：" + state.ids[i]);
    if (!callBool(cls("NN.PF.UI.Trade", "UITrade"), ui, "SetTrade", [newString(state.ids[i])]))
      throw new Error("游戏没有接受所选鱼：" + state.ids[i]);
  }
  const selected = ui.add(fieldOffset(cls("NN.PF.UI.Trade", "UITrade"), "exportFishList")).readPointer();
  const selectedCount = selected && !selected.isNull() ? il2cpp.arrayLength(selected) : 0;
  if (selectedCount !== state.ids.length) throw new Error("交易列表接受数量不符：" + selectedCount + "/" + state.ids.length);
  state.before = new Set(beforeFish.map(function (fish) { return fish.id; }));
  state.ui = ui;
  callVoid(cls("NN.PF.UI.Trade", "UITrade"), ui, "Export", []);
  state.deadline = Date.now() + 60000;
}

function checkTradeExport(state) {
  const current = readFishList();
  const stillOwned = new Set(current.map(function (fish) { return fish.id; }));
  const missing = state.ids.filter(function (id) { return state.before.has(id) && !stillOwned.has(id); });
  if (missing.length === 0) return;
  const ui = state.ui;
  if (ui && !ui.isNull()) {
    // 导出完成后刷新游戏交易列表和仓库状态，不打开可见功能窗口。
    try { callVoid(cls("NN.PF.UI.Trade", "UITrade"), ui, "Refresh", []); } catch (error) {}
  }
  pending = null;
  actions++;
  lastActionAt = Date.now();
  sendResult({
    cmd: "tradeexport",
    ok: missing.length === state.ids.length,
    id: "",
    msg: "游戏鱼群已移除 " + missing.length + "/" + state.ids.length + " 条导出鱼；Steam 库存回显以游戏/Steam 库存为准。",
    errorKind: missing.length === state.ids.length ? "" : "game_state",
    terminal: true
  });
}

function checkMerge(state) {
  const ui = state.ui;
  if (!ui || ui.isNull()) { failPending("合成对象已失效"); return; }
  const afterList = readFishList();
  const after = {};
  afterList.forEach(function (fish) { after[fish.id] = fish; });
  let removed = 0;
  Object.keys(state.before).forEach(function (id) { if (!after[id]) removed++; });
  const added = Object.keys(after).filter(function (id) { return !state.before[id]; });
  const beforeCount = Object.keys(state.before).length;
  const afterCount = afterList.length;
  const resultFish = readResultFish(ui, uiMergeClass());
  const done = added.length > 0 || (beforeCount - afterCount) >= 9 || (resultFish && resultFish.id);
  if (!done) return;
  closeMergeResultUi(ui);
  try { callVoid(uiMergeClass(), ui, "ResetParent", []); } catch (error) {}
  pending = null;
  actions++;
  lastActionAt = Date.now();
  const ok = added.length > 0 && removed >= 10;
  sendResult({
    cmd: "merge",
    ok: ok,
    isNew: true,
    id: resultFish && resultFish.id ? resultFish.id : (added[0] || ""),
    msg: ok
      ? "合成已确认：消耗 " + removed + " 条，新增 " + added.length + " 条（鱼群 " + beforeCount + "→" + afterCount + "）"
      : "合成回包后鱼群未核实（鱼群 " + beforeCount + "→" + afterCount + "）",
    errorKind: ok ? "" : "game_state",
    requestId: "",
    terminal: true
  });
}

function setupBreed(state) {
  const ui = findFirst(uiBreedClass());
  if (!ui) throw new Error("找不到游戏的繁育对象");
  const type = uiBreedClass();
  state.ui = ui;
  callVoid(type, ui, "ResetParentFishList", []);
  const a = callBool(type, ui, "SetParent", [newString(state.parentA)]);
  const b = callBool(type, ui, "SetParent", [newString(state.parentB)]);
  if (!a || !b) throw new Error("游戏繁育槽未接受亲鱼");
  state.before = {};
  readFishList().forEach(function (fish) { state.before[fish.id] = fish; });
  callVoid(type, ui, "Breed", []);
  state.deadline = Date.now() + 55000;
}

function checkBreed(state) {
  const ui = state.ui;
  if (!ui || ui.isNull()) { failPending("繁育对象已失效"); return; }
  const afterList = readFishList();
  const after = {};
  afterList.forEach(function (fish) { after[fish.id] = fish; });
  const parentA = after[state.parentA];
  const parentB = after[state.parentB];
  const oldA = state.before[state.parentA];
  const oldB = state.before[state.parentB];
  const parentsConsumed = !!(parentA && oldA && parentA.bc < oldA.bc && parentB && oldB && parentB.bc < oldB.bc);
  const added = Object.keys(after).filter(function (id) { return !state.before[id]; });
  const resultFish = readResultFish(ui, uiBreedClass());
  const done = parentsConsumed || added.length > 0 || (resultFish && resultFish.id);
  if (!done) return;
  closeBreedResultUi(ui);
  try { callVoid(uiBreedClass(), ui, "ShowBreedTimer", []); } catch (error) {}
  pending = null;
  actions++;
  lastActionAt = Date.now();
  const childId = resultFish && resultFish.id ? resultFish.id : (added[0] || "");
  const ok = parentsConsumed && (!!childId || added.length > 0);
  sendResult({
    cmd: "breed",
    ok: ok,
    isNew: true,
    id: childId,
    msg: ok
      ? "繁育已确认：亲鱼次数已下降，新鱼 " + (childId || "已出现")
      : "繁育回包后亲鱼次数或新鱼未核实",
    errorKind: ok ? "" : "game_state",
    requestId: state.requestId || "",
    terminal: true
  });
}

function runUpgrade(state) {
  const manager = dataManager();
  if (!manager) { failPending("游戏数据尚未就绪"); return; }
  const type = gameDataClass();
  const before = manager.add(fieldOffset(type, "tankLevel")).readInt();
  try {
    callVoid(type, manager, "UpgradeTankLevel", []);
  } catch (error) { failPending("游戏升级入口失败：" + (error.message || error), "game_state"); return; }
  const after = manager.add(fieldOffset(type, "tankLevel")).readInt();
  pending = null;
  actions++;
  lastActionAt = Date.now();
  sendResult({ cmd: "upgrade", ok: after > before, msg: "鱼缸等级 " + before + "→" + after, errorKind: after > before ? "" : "game_state", terminal: true });
}

/** 只读演练：把材料放进隐藏合成槽，确认游戏接受，然后清空。不会发请求。 */
function runPreviewMerge(state) {
  const ui = findFirst(uiMergeClass());
  if (!ui) { failPending("找不到游戏的合成对象"); return; }
  const type = uiMergeClass();
  try {
    callVoid(type, ui, "ResetParent", []);
    let accepted = 0;
    for (let i = 0; i < state.ids.length; i++) {
      if (callBool(type, ui, "SetParent", [newString(state.ids[i])])) accepted++;
    }
    const filled = mergeSlotCount(ui);
    callVoid(type, ui, "ResetParent", []);
    pending = null;
    sendResult({
      cmd: "previewmerge", ok: filled === 10,
      msg: "隐藏合成槽演练：游戏接受 " + accepted + "/10，槽内 " + filled + "/10；已清空，未发送合成请求",
      errorKind: filled === 10 ? "" : "game_state", terminal: true
    });
  } catch (error) {
    failPending("合成槽演练失败：" + (error.message || error));
  }
}

function readInteraction() {
  try {
    const ui = findFirst(uiManagerClass());
    if (!ui) return { networkBusy: false, windowOpen: false, inputBlocked: false };
    const type = uiManagerClass();
    let inputBlocked = false;
    try {
      const audio = findFirst(cls("NN.PF.Core.Managers", "AudioManager"));
      if (audio) inputBlocked = audio.add(fieldOffset(cls("NN.PF.Core.Managers", "AudioManager"), "isBlockUi")).readU8() !== 0;
    } catch (error) { inputBlocked = false; }
    return {
      networkBusy: gameObjectActive(ui.add(fieldOffset(type, "viewNetwork")).readPointer()),
      windowOpen: gameObjectActive(ui.add(fieldOffset(type, "viewWindow")).readPointer()),
      menuOpen: ui.add(fieldOffset(type, "currentMenuType")).readInt() !== 0,
      inputBlocked: inputBlocked,
      processing: false
    };
  } catch (error) { return { networkBusy: false, windowOpen: false, inputBlocked: false, processing: false }; }
}

/** Check the game session without exposing its token or any credential material. */
function networkSessionStatus() {
  try {
    const steam = cls("", "SteamManager");
    if (steam && !steam.isNull()) {
      const initialized = method(steam, "get_Initialized", 0);
      if (initialized && !initialized.isNull() && !callBool(steam, NULL, "get_Initialized", []))
        return { ready: false, reason: "steam_not_initialized" };
    }
    const gameNetworkType = cls("NN.PF.Core.Network", "NetworkManager");
    if (!gameNetworkType || gameNetworkType.isNull())
      return { ready: false, reason: "game_network_type_unavailable" };
    const gameNetwork = singleton(gameNetworkType);
    if (!gameNetwork) return { ready: false, reason: "game_network_manager_unavailable" };
    const getNetwork = method(gameNetworkType, "GetNetworkManager", 0);
    if (!getNetwork || getNetwork.isNull())
      return { ready: false, reason: "transport_getter_unavailable" };
    const network = invoke(getNetwork, gameNetwork, null);
    if (!network || network.isNull()) return { ready: false, reason: "transport_unavailable" };
    const transportType = cls("NANOOGame.Network", "NetworkManager");
    const tokenOffset = fieldOffset(transportType, "accessToken");
    if (tokenOffset === null || tokenOffset === undefined)
      return { ready: false, reason: "session_field_unavailable" };
    const token = readString(network.add(tokenOffset).readPointer());
    // Never return or log the token; only its presence is used as a readiness signal.
    // accessToken 托管字段实测恒为空（真 JWT 只经 SetRequestHeader 下发，
    // 存在 native 侧）。用它判断会话会让助手永远停在「等待助手确认游戏会话」，
    // 一个动作都不发 —— 这正是「能读到数据但动作不生效」的来源。
    // 能读到鱼群本身就说明登录已完成，这里降级放行。
    if (token && token.length > 0) return { ready: true, reason: "session_verified" };
    return { ready: true, reason: "session_token_unreadable" };
  } catch (error) {
    // 读字段失败同样不等于未登录，不能让动作永久卡死。
    return { ready: true, reason: "session_check_error" };
  }
}

function buildTick() {
  let fish = [];
  let counter = null;
  try { fish = readFishList(); counter = readCounters(); } catch (error) { counter = null; }
  const interaction = readInteraction();
  const session = networkSessionStatus();
  // 输入锁带抖动：按钮音效、面板动画都会瞬间把 isBlockUi 置 true。
  // 原来只要读到一次阻塞就把稳定计时清零，结果是永远「正在等待稳定」。
  // 现在只有阻塞连续存在超过容忍窗口才算真阻塞。
  if (interaction.inputBlocked) {
    if (!inputBlockedSince) inputBlockedSince = Date.now();
  } else inputBlockedSince = 0;
  const blockedLongEnough = inputBlockedSince > 0 && Date.now() - inputBlockedSince >= INPUT_BLOCK_GRACE_MS;
  if (!session.ready || interaction.networkBusy || blockedLongEnough) networkSessionStableAt = 0;
  else if (!networkSessionStableAt) networkSessionStableAt = Date.now();
  const networkReady = networkSessionStableAt > 0 && Date.now() - networkSessionStableAt >= SESSION_STABLE_MS;
  const networkReadyReason = !session.ready ? session.reason
    : interaction.networkBusy ? "network_request_busy"
    : blockedLongEnough ? "game_input_blocked"
    : !networkReady ? "session_stabilizing" : "ready";
  let breedable = 0, exhausted = 0;
  fish.forEach(function (item) { if (item.bc > 0) breedable++; else exhausted++; });
  return {
    type: "tick",
    game: !!counter,
    armed: armed,
    cfg: true,
    actions: actions,
    total: fish.length,
    breedable: breedable,
    exhausted: exhausted,
    maxMerge: 0,
    charge: counter ? counter.charge : -1,
    tankLevel: counter ? counter.tankLevel : -1,
    tankExp: counter ? counter.tankExp : -1,
    canLevelUp: counter ? counter.canLevelUp : false,
    secToHeart: secondsToHeart(counter, Math.floor(Date.now() / 1000)),
    // viewWindow 是最外层主容器，Home 页也处于 active；用它判忙会让自动合成永远停住。
    gameBusy: interaction.networkBusy || blockedLongEnough || !!pending || !networkReady,
    inputBlocked: blockedLongEnough,
    inputBlockedPulse: !!interaction.inputBlocked,
    networkBusy: interaction.networkBusy,
    networkReady: networkReady,
    networkReadyReason: networkReadyReason,
    functionWindowOpen: !!interaction.menuOpen,
    serverInventoryReady: networkReady,
    breedPending: !!pending && pending.kind === "breed",
    pendingRequestId: pending && pending.kind === "breed" ? (pending.requestId || "") : "",
    actionWaitSeconds: lastActionAt ? Math.max(0, Math.ceil(MAIN_INTERVAL_SECONDS - (Date.now() - lastActionAt) / 1000)) : 0,
    windowLocked: false,
    windowLockReason: "",
    windowChecksOk: 0
  };
}

/** Resolve state reads on the Unity main thread instead of the worker's RPC thread. */
function serviceSnapshotRequests() {
  if (pendingSnapshots.length === 0) return;
  const requests = pendingSnapshots.splice(0, pendingSnapshots.length);
  requests.forEach(function (request) {
    if (request.done) return;
    request.done = true;
    if (request.timer) clearTimeout(request.timer);
    try { request.resolve(snapshot()); }
    catch (error) { request.resolve({ type: "state", ok: false, msg: "主线程读取失败" }); }
  });
}

function snapshot() {
  try {
    const fish = readFishList();
    const counter = readCounters();
    return {
      type: "state",
      ok: true,
      count: fish.length,
      fish: fish,
      collectionTypes: readCollection(),
      charge: counter ? counter.charge : -1,
      tankLevel: counter ? counter.tankLevel : -1,
      tankExp: counter ? counter.tankExp : -1,
      canLevelUp: counter ? counter.canLevelUp : false,
      source: "frida"
    };
  } catch (error) {
    return { type: "state", ok: false, msg: "游戏数据尚未就绪：" + (error && error.message ? error.message : error) };
  }
}

if (!hookMainThread()) {
  // 游戏可能还没进主循环（GameManager.Update 尚未可用），此时挂不上钩子。
  // 原来失败一次就永久失败，表现为「游戏主线程暂未就绪」、鱼群读不到。
  // 在 JS 线程定时重试：IL2CPP 反射只能在这个线程调，不能在游戏主线程调。
  let attempts = 0;
  const retry = setInterval(function () {
    attempts++;
    if (hookMainThread() || attempts >= 90) clearInterval(retry);
  }, 2000);
}

// 鉴权采集在 agent 内不可行（实测结论）：
// 只要在脚本加载阶段新增任何一次 IL2CPP 调用（无论 assembly_open 还是
// domain_get_assemblies），GameManager.Update 的钩子就会失效 ——
// 表现为 pump 不跑、界面报「游戏主线程暂未就绪」、鱼群读不到。
// 因此这里一律不调用：auth() 返回空，控制器自动回退游戏内动作路径。
// 需要 HTTP 直发时，由外部工具单独抓一次 header（见 PCFish-API协议.md）。

rpc.exports = {
  version: function () { return { agent: AGENT_VERSION, protocol: PROTOCOL_VERSION, hooked: updateHooked }; },
  /** 取鉴权 header，供控制器在游戏进程外直发 HTTP。 */
  auth: function () {
    try { return { ok: true, headers: requestAuthHeaders() }; }
    catch (error) { return { ok: false, reason: String(error), headers: {} }; }
  },
  /** 卸载钩子后再让 worker 断开，避免被中断的 Update 调用留在栈上。 */
  prepareDetach: function () {
    try { if (updateHook) updateHook.detach(); } catch (error) {}
    try { if (authHook) authHook.detach(); } catch (error) {}
    updateHook = null;
    authHook = null;
    updateHooked = false;
    return true;
  },
  snapshot: function () {
    return new Promise(function (resolve) {
      const request = { resolve: resolve, done: false, timer: null };
      request.timer = setTimeout(function () {
        if (request.done) return;
        request.done = true;
        resolve({ type: "state", ok: false, msg: "游戏主线程暂未就绪" });
      }, 8000);
      pendingSnapshots.push(request);
    });
  },
  tick: function () {
    return cachedTick || {
      type: "tick", game: false, gameBusy: true, networkBusy: false,
      inputBlocked: false, networkReady: false, serverInventoryReady: false,
      breedPending: false, actionWaitSeconds: 0
    };
  },
  setArmed: function (value) { armed = !!value; return armed; },
  setFrameLimit: function (value) {
    const v = parseInt(value, 10);
    frameLimitRequest = (v === 10 || v === 30) ? v : 0;
    return { accepted: true, frameLimit: frameLimitRequest };
  },
  startMerge: function (ids) {
    if (!updateHooked) return { accepted: false, reason: "主线程钩子未建立" };
    if (!ids || ids.length !== 10) return { accepted: false, reason: "需要 10 条材料" };
    if (pending) return { accepted: false, reason: "上一笔动作尚未完成" };
    pending = { kind: "merge", state: "setup", ids: ids.slice(), deadline: Date.now() + 10000 };
    return { accepted: true };
  },
  previewMerge: function (ids) {
    if (!updateHooked) return { ok: false, reason: "主线程钩子未建立" };
    if (!ids || ids.length !== 10) return { ok: false, reason: "需要 10 条材料" };
    if (pending) return { ok: false, reason: "上一笔动作尚未完成" };
    pending = { kind: "previewMerge", state: "setup", ids: ids.slice(), deadline: Date.now() + 10000 };
    return { ok: true };
  },
  startBreed: function (requestId, parentA, parentB) {
    if (!updateHooked) return { accepted: false, reason: "主线程钩子未建立" };
    if (!parentA || !parentB || parentA === parentB) return { accepted: false, reason: "需要两条不同亲鱼" };
    if (pending) return { accepted: false, reason: "上一笔动作尚未完成" };
    pending = { kind: "breed", state: "setup", requestId: requestId || "", parentA: parentA, parentB: parentB, deadline: Date.now() + 10000 };
    return { accepted: true };
  },
  startUpgrade: function () {
    if (!updateHooked) return { accepted: false, reason: "主线程钩子未建立" };
    if (pending) return { accepted: false, reason: "上一笔动作尚未完成" };
    pending = { kind: "upgrade", state: "setup", deadline: Date.now() + 10000 };
    return { accepted: true };
  },
  startTradeExport: function (ids) {
    if (!updateHooked) return { accepted: false, reason: "主线程钩子未建立" };
    if (!ids || ids.length < 1) return { accepted: false, reason: "没有选中鱼" };
    if (pending) return { accepted: false, reason: "上一笔动作尚未完成" };
    pending = { kind: "tradeexport", state: "setup", ids: ids.slice(), deadline: Date.now() + 10000 };
    return { accepted: true };
  },
  actionStatus: function () {
    return pending ? { pending: true, kind: pending.kind, state: pending.state, requestId: pending.requestId || "" }
      : { pending: false };
  },
  ping: function () { return { ok: true, hooked: updateHooked, frame: frame }; }
};

// 兼容小写 RPC 调用方（frida-python 的 exports_sync 会规范化方法名）。
rpc.exports.setarmed = rpc.exports.setArmed;
rpc.exports.setframelimit = rpc.exports.setFrameLimit;
rpc.exports.startmerge = rpc.exports.startMerge;
rpc.exports.previewmerge = rpc.exports.previewMerge;
rpc.exports.startbreed = rpc.exports.startBreed;
rpc.exports.startupgrade = rpc.exports.startUpgrade;
rpc.exports.starttradeexport = rpc.exports.startTradeExport;
rpc.exports.actionstatus = rpc.exports.actionStatus;
rpc.exports.preparedetach = rpc.exports.prepareDetach;
