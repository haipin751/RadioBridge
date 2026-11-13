[English](README.md) | **简体中文**

# RadioBridge v3.0（《Radio Commander》回合制无线电桥接插件）

**RadioBridge** 是一套为战术策略游戏《Radio Commander（无线电指挥官）》打造的 **BepInEx 5 插件 + 回合制 Python 控制脚本 + AI Agent 技能包**。它在游戏内开启本地 HTTP 服务（`127.0.0.1:8765`），让玩家或 AI 大模型能够纯靠无线电通讯记录、任务目标与地图坐标，以严格的**回合制**方式指挥部队。

> **免责声明**：本仓库**仅包含**原创补丁源码、控制脚本、Skill 定义与示例日志，**不包含任何游戏原文件、官方程序集或版权资源**。

---

## 一、各组件如何搭配工作

整个方案由三部分协同配合，形成“**游戏插件提供接口 → Python 脚本阻塞同步 → AI / 玩家回合制决策**”的闭环：

```text
+-----------------------------------+
|  AI Agent (加载 skill/SKILL.md)   |  <-- 思考期间游戏始终处于 0x 暂停
|  或 人类指挥官 (终端手动输入命令)  |
+-----------------+-----------------+
                  | 调用 CLI
                  v
+-----------------------------------+
|  scripts/rc_cli.py & rc_map.py    |  <-- 本地 sleep 阻塞等待通话结束，避免空转轮询
+-----------------+-----------------+
                  | HTTP (127.0.0.1:8765)
                  v
+-----------------------------------+
|  RadioBridge.dll (BepInEx 5 插件) |  <-- 主线程操作游戏：读目标/日志/坐标、下令、切倍速
+-----------------------------------+
                  | 注入运行
                  v
+-----------------------------------+
|     《Radio Commander》游戏本体    |
+-----------------------------------+
```

1. **游戏端补丁（[src/RadioBridge.cs](src/RadioBridge.cs)）**
   - 编译为 `RadioBridge.dll` 后放入游戏 `BepInEx\plugins\` 目录。
   - 负责与游戏内部对象交互：读取 `ObjectivesController`（任务目标）、`RadioController`（电台记录与动态菜单）、地图坐标，并在后台强制保活（`Application.runInBackground = true`）。
2. **命令行控制脚本（[scripts/rc_cli.py](scripts/rc_cli.py) 与 [scripts/rc_map.py](scripts/rc_map.py)）**
   - **零第三方依赖**（仅使用 Python 标准库）。
   - `rc_map.py`：调用 `/map` 获取地图边界、地标（Waypoints）、任务标注点与友军当前网格坐标。
   - `rc_cli.py`：提供 `status`、`send`、`fast` 三个命令。每次命令返回前都会**自动将游戏暂停在 `0x`**；在 `send`（`1x` 播放）或 `fast`（`2x` 快进）期间在本地 `sleep` 等待电台对话结束，再自动暂停并返回最新日志。
3. **AI 技能配置（[skill/SKILL.md](skill/SKILL.md)）**
   - 如果你想让 AI 自动接管战役，可将 `skill/SKILL.md` 及 `scripts/` 放入支持 Agent Skills 的工具（如 Antigravity 等）的技能目录中。
   - AI 会严格遵循其中规则：**开局先读地图坐标 → 查看状态 → 匹配当前语言选项发令 → 本地阻塞等待回电/快进 → 自动暂停进入下一回合**。

---

## 二、目录结构

```text
RadioBridge/
├── src/
│   └── RadioBridge.cs          # BepInEx 5 单文件 C# 插件源码（HTTP 端口 8765）
├── scripts/
│   ├── rc_cli.py               # 回合制阻塞控制脚本（status / send / fast）
│   └── rc_map.py               # 独立地图坐标读取脚本（/map）
├── skill/
│   └── SKILL.md                # AI Agent 技能说明文件（可选，用于对接 AI 自动指挥）
├── examples/
│   └── mission1_test_logs.md   # 第一关高难度真实补给实战电台日志示例（去重 72 条）
├── build.ps1                   # PowerShell 一键编译与部署脚本（引用本地正版游戏 DLL）
├── LICENSE                     # MIT 开源协议
├── README.md                   # 英文说明文档
└── README.zh-CN.md             # 中文说明文档（本文件）
```

---

## 三、环境准备与编译部署

### 前置条件
1. 正版《Radio Commander》（Windows PC 版）
2. 已在游戏目录安装 **BepInEx 5 (x64)**（安装后先启动一次游戏以生成 `BepInEx\plugins` 文件夹）
3. **Python 3.8+**（用于运行 `rc_cli.py` 和 `rc_map.py`）
4. Windows 自带的 **.NET Framework 4.x**（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）

### 编译与安装插件
在仓库根目录打开 PowerShell，将 `-GameDir` 替换为你的游戏安装目录：

```powershell
# 仅编译（输出到 ./build/RadioBridge.dll）
.\build.ps1 -GameDir "D:\Games\Radio Commander"

# 编译并自动部署到 <游戏目录>\BepInEx\plugins\RadioBridge.dll
.\build.ps1 -GameDir "D:\Games\Radio Commander" -Deploy
```

### 游戏内重要提示
- 启动游戏并进入战役后，**务必确认帐篷界面里的电台开关处于开启状态**；如果游戏内电台处于关闭状态，游戏自身的 `RadioController` 会屏蔽无线电通讯与底部通话记录面板。

---

## 四、常用控制命令（脚本搭配用法）

脚本默认连接 `http://127.0.0.1:8765`（可通过环境变量 `RC_BRIDGE_URL` 修改）。

### 1. 读取地图与单位坐标
开局或需要规划行军路线时运行：
```bash
python scripts/rc_map.py
```
返回地图边界（`bounds`）、地标坐标（`waypoints`）、任务目标坐标（`objectives`）及友军当前坐标（`units`）。

### 2. 查看当前回合状态（自动暂停）
```bash
python scripts/rc_cli.py status
```
若当前有单位正在讲话，会先等待本句说完再自动暂停（`0x`），并返回当前 `objectives`、`recent_logs`（最近 5 条通话）、`dialog_open` 及当前游戏语言对应的 `options`。

### 3. 发送无线电指令或回复剧情弹窗
`options` 中的关键字会随游戏当前语言（中文/英文等）动态变化，请直接匹配 `options` 中返回的词，或传入从 1 开始的序号：

```bash
# 简写位置参数：<单位> <分组> <命令> [参数]
# 中文界面示例：
python scripts/rc_cli.py send alpha 报告 位置
python scripts/rc_cli.py send alpha 命令 移动 245,927

# 英文界面示例：
python scripts/rc_cli.py send alpha Report Position
python scripts/rc_cli.py send alpha Order Move 245,927

# 完整参数写法（选项包含空格时请加引号，例如 "Fight status"）
python scripts/rc_cli.py send --unit alpha --group Order --order Move --param "245,927"

# 回复剧情对话弹窗（当 dialog_open 为 true 时，传入 1-based 序号）
python scripts/rc_cli.py send --dialog 1
```

### 4. 快进等待下一条通讯
```bash
python scripts/rc_cli.py fast
```
自动切换到 `2x` 加速档并在本地 `sleep`，一旦有新单位呼叫或任务目标更新，自动切回 `1x` 听完本轮通话后再暂停（`0x`）。

---

## 五、与 AI Agent 搭配使用

如果你想让 AI 替你指挥战役：
1. 确保游戏已打开并进入任务（且游戏内电台开关已打开）。
2. 将本仓库中的 `skill/SKILL.md` 与 `scripts/` 目录放到你的 AI Agent 技能路径中（例如 `~/.gemini/config/skills/radio-commander-bridge/`），并将 `SKILL.md` 中的脚本路径调整为你的实际路径。
3. 直接对 AI 说：“**接管指挥完成当前战役**”，AI 就会自动调用 `rc_map.py` 和 `rc_cli.py` 按回合制完成侦察、移动、交战、弹药管理与撤离。
4. 实战效果可参考示例日志：[examples/mission1_test_logs.md](examples/mission1_test_logs.md)。

---

## 六、HTTP 接口一览（`127.0.0.1:8765`）

| 接口 | 说明 |
| :--- | :--- |
| `GET /` 或 `/help` | 返回基础接口说明 |
| `GET /status`（别名 `/state`） | 返回当前任务目标、最近电台记录、`comm_busy`、`dialog_open` 与动态 `options` |
| `GET /send`（别名 `/say`） | 执行无线电指令（`unit`/`group`/`order`/`param`）、剧情回复（`dialog`）或倍速切换（`speed`: `0`/`1`/`2`） |
| `GET /map` | 返回地图边界、地标坐标、任务目标坐标与友军单位坐标 |

---

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。
