# PCFish 助手连接方式

## 结论

助手现在有两条连接方式，都能读取鱼群、发起合成和繁育：

| 方式 | 怎么连 | 是否写游戏目录 |
|---|---|---|
| **Frida 内存连接**（默认） | 按 PCFish.exe 的 PID 附加，在游戏进程里加载脚本 | 不写。不向 BepInEx\plugins 放 DLL |
| **插件桥接**（兼容） | 游戏内 BepInEx 插件开 127.0.0.1:27777 | 需要 PCFishAutoHelper.dll |

设置页「游戏连接」可选择「自动（优先 Frida）」「Frida 内存连接」「插件桥接」。
Frida worker 等 PCFish 进程启动 60 秒后再附加；助手还会等 Steam 初始化、游戏会话令牌存在并且网络空闲稳定 30 秒后才放行动作。自动模式至少等待 90 秒，并确认游戏已运行超过启动缓冲期后才考虑插件回退。

## 实测情况

在与 TBH 1.3.82 相同的工作方式下，以下步骤已经在本机跑通：

- 按 PID 附加 PCFish.exe，读取 GameDataManager 的完整鱼群（891 条）、繁育计数器、鱼缸等级和经验
- 隐藏 UIMerge 对象登记 10 条材料并调用游戏原生 Merge()，核对鱼群从 891 降到 882
- 隐藏 UIBreed 对象登记亲鱼并调用游戏原生 Breed()，核对亲鱼次数下降、新鱼出现
- 后台静默运行时游戏窗口保持可用，不弹功能窗口

## 这不是完全脱离游戏进程

TBH 的无插件连接同样不部署插件文件，但会通过 Frida 向游戏进程加载代码。
PCFish 的 Frida 模式也是进程内加载：桌面助手启动 PCFishFrida.exe，后者附加游戏并执行内存脚本。
它不会修改游戏安装目录，但并不能绕过游戏客户端或服务端校验。

## 为什么不直接照抄 TBH 的 HTTP 接口

TBH 的桌面端连的是它自己的 BepInEx 插件端口 127.0.0.1:19090，并没有公开一个独立于进程的官方 API。
PCFish 的服务端地址（gapi.nanoocompany.com）和 /cs/pcfish/breed/run、/cs/pcfish/merge/run
接口都依赖 Steam 登录后生成的会话令牌；自行重放这些请求需要维护登录和令牌刷新，风险高于复用游戏自己的入口。

因此 Frida 模式仍然调用游戏自身的 UIMerge.Merge()、UIBreed.Breed() 和 GameDataManager，
不另写一套服务端协议。
