using System;
using UnityEngine;

namespace PCFishAutoHelper;

/// <summary>
/// 主线程心跳。**故意什么都不画** —— 没有 OnGUI、没有 GUI、没有任何 Input 读取。
///
/// 上一版就是在这个文件里画了 F8 面板，用户直接判定"你把游戏窗口改了"。
/// 现在它的全部职责只有一条：每帧驱动桥接（读游戏状态、执行外部下发的命令）。
/// 所有界面都在游戏之外的控制器 exe 里。
/// </summary>
public class AutoHelperBehaviour : MonoBehaviour
{
    public AutoHelperBehaviour(IntPtr pointer) : base(pointer) { }

    private bool _inited;

    private void Update()
    {
        try
        {
            if (!_inited)
            {
                _inited = true;

                // 游戏窗口不在前台时 Unity 可能暂停 Update。用户经常切到别的程序去，
                // 不设这一项自动繁殖就会静默停摆 —— 这是必须做的一步。
                try
                {
                    if (!Application.runInBackground)
                    {
                        Application.runInBackground = true;
                        Journal.Write("已置 Application.runInBackground = true（窗口不在前台也继续工作）");
                    }
                }
                catch (Exception ex)
                {
                    Journal.Error("设置 runInBackground 失败", ex);
                }
            }

            // 网络回包的游戏数据更新在主线程完成，再处理下一条桥接命令。
            GameBridge.DrainMainThreadWork();

            BridgeServer.Pump();
        }
        catch (Exception ex)
        {
            Journal.Error("主循环异常", ex);
        }
    }
}
