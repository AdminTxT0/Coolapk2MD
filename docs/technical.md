# 实现细节与踩坑记录

这份文档记录 Coolapk2MD 在实现过程中确认的事实、做过的实验和踩过的坑，
供后续维护者参考，也避免重复试错。

---

## 一、酷安网页版的访问限制

### 1. 壳页机制

直接请求 `https://www.coolapk.com/feed/{id}`（不带分享令牌）时，
服务端返回的是一个约 3 KB 的壳页，正文位置是：

```html
<div class="message-title">
  <p>您当前查看的是「图文分享」，请用酷安手机APP扫码查看详情</p>
</div>
```

带 `?s=` 令牌时返回完整页面（约 40 KB）。

### 2. 令牌的作用范围

做过的对照实验：

| 变量 | 结果 |
| --- | --- |
| 换 UA 为 Googlebot / Bingbot / baiduspider | 壳页 |
| 加 `shareFrom` 参数 | 壳页 |
| 改用移动端 UA | 壳页 |
| 同一 token 换到另一个 feed id | 壳页 |
| 同一 token，换 IP（不同网络） | **正常** |
| 同一 token，换 UA（普通浏览器） | **正常** |

结论：令牌与 **feed id** 绑定，不与 UA、IP 绑定。
令牌形如 `NTQyNjQ4MDI0Y2VmMmVnNmFlOWI1ZGF6a1670`，区分大小写。

### 3. 其他入口不可用

| 入口 | 结果 |
| --- | --- |
| `api.coolapk.com/v6/feed/detail?id=` | 403（需 App 签名头 `X-App-Token` 等） |
| `www.coolapk.com/feed/detail?id=` | 404 |
| `www.coolapk.com/u/{uid}` | 壳页 |
| `www.coolapk.com/` | 壳页 |

因此工具没有走 API，而是解析分享链接对应的 HTML。

---

## 二、DOM 结构

### 图文长文

| 内容 | 选择器 |
| --- | --- |
| 标题 | `div.message-title > p` |
| 作者 | `div.common-userinfo-group div.username-item p`（第 1 个） |
| 时间 / 机型 | 同上（第 2 个，机型在 `<span>` 里） |
| 封面 | `div.message-cover > img` |
| 正文 | `div.feed-article-message` |
| 正文配图 | `div.message-image-item > img.message-image` |
| 超链接 | `a.feed-link-url` |

### 普通动态

正文容器换成 `div.feed-message`，并且**配图不在正文容器内**，
而是在它的兄弟节点 `div.message-image-group` 里。这是最容易漏的一点：
只抓 `feed-message` 会得到没有配图的正文。

### 表情包

正文里的 `<img class="feed-emoji" src="https://static.coolapk.com/emoticons/...">`
是表情，不是配图。工具不下载它们，按 `alt` 文本保留（如「流泪」），
否则图片列表里会混进几十张 20×20 的表情图。

### 图片 URL 变体

酷安图片常见形式：

```
https://.../xxx.jpeg.m.jpg
https://.../xxx.jpeg@642x1852.jpeg.m.jpg
```

测试结果：**去掉 `.m.jpg` 后缀**能拿到原图；
`@642x1852` 这类尺寸后缀也可以完全去掉，直接请求原图路径即可。
其他变体（如把 `.m.jpg` 换成 `.l.jpg`）多数返回 404。

### 引用其它文章的链接

正文里指向其它酷安文章的 `<a>`，作者贴的通常是 App 分享出来的完整链接：

```
https://www.coolapk.com/feed/67928960?s=MDc3MTBiYmIyMGVjNDE1ZzZhMWE1YjZhega1454&shareUid=34522133&shareFrom=com.coolapk.market_14.5.4
```

令牌在链接里现成可用，**不需要重新分享**。抽出来时只保留 `feed/{id}?s={token}`，
去掉 `shareUid`、`shareFrom` 这些跟踪参数：

```
FEED_ID_RE = r"coolapk\.com/feed/(\d+)"
TOKEN_RE   = r"[?&]s=([A-Za-z0-9%_\-]+)"
```

实测 `feed/72078845` 正文有 8 个这样的链接，逐一抓取全部成功；
其中被引用的文章自己也引用了别的动态，第 2 层同样能拿到。
只有裸 `feed/{id}`（不带 `?s=`）才会退回壳页 —— 这是递归导出唯一的死角。

递归策略是广度优先 + 按 feed id 去重 + 深度/数量上限（3 层 / 60 篇），
避免一篇文章引出一整个论坛。子文章与主文章平级放在同一个输出根目录下。

---

## 三、WinForms 界面的坑

### 1. `Icon.ToBitmap()` 解析不了 PNG 帧 ICO

第一版图标是 256×256 + PNG 压缩帧（Vista 之后的标准做法，资源管理器显示很清晰）。
但 .NET Framework 的 `Icon.ToBitmap()` 在这种 ICO 上会抛：

```
System.ArgumentOutOfRangeException: 参数超出范围
   at System.Drawing.Icon.BmpFrame()
```

原因是它只认 BMP 帧格式。

解决：

- 图标改为 **16/32/48/64 四个 BMP 帧**（csc 的资源编译器同样不接受 PNG 帧，报 `CS1567`）
- 界面里的 logo 不依赖 ICO 解码，直接用 GDI+ 按同一构图现画（`MakeLogo()`），
  与图标视觉一致但不受 ICO 格式影响

### 2. `Graphics.DpiX` 返回缓存值

在 `SetProcessDPIAware()` 之后：

| 检测方式 | 结果 |
| --- | --- |
| 屏幕逻辑分辨率 | 1707×1067（虚拟化值） |
| 屏幕物理分辨率 | 2560×1600 |
| `GetDeviceCaps(dc, LOGPIXELSX)` | **144** ✅ |
| `Graphics.FromHwnd(IntPtr.Zero).DpiX` | **96** ❌（陈旧缓存） |

所以 DPI 系数必须用 `GetDeviceCaps` 直接问 GDI：

```csharp
IntPtr dc = GetDC(IntPtr.Zero);
int dpi = GetDeviceCaps(dc, 88); // LOGPIXELSX
ReleaseDC(IntPtr.Zero, dc);
float scale = dpi / 96f;
```

### 3. AutoScaleMode 会算出反向因子

同时设置 `SetProcessDPIAware()` 和 `AutoScaleMode = AutoScaleMode.Dpi` 时，
WinForms 把设计尺寸 820×700 缩放成了 561×504（因子 ≈ 1/1.46，方向反了）。

最终方案：`AutoScaleMode = None`，所有尺寸手动乘 DPI 系数：

```csharp
float k = DetectScale();
int S(int v) { return (int)Math.Round(v * k); }
```

### 4. 测量窗口大小时注意测量进程自己的 DPI 感知

用 PowerShell 的 `GetWindowRect` 去量一个 DPI-aware 程序的窗口，
如果测量脚本自己不是 DPI-aware，拿到的会是**虚拟化后的坐标**
（1230×1050 被报成 820×700），误以为布局没生效。

测量脚本开头必须先调用：

```powershell
[DllImport("user32.dll")] static extern bool SetProcessDPIAware();
```

### 5. TableLayoutPanel 未设行样式会按 AutoSize 处理

「保存位置」这一行用了一个嵌套的 `TableLayoutPanel`（`t2row`）做左右分栏。
最初只设了 `RowCount = 1` 而没有加 `RowStyles`，结果 TableLayoutPanel
默认按 **AutoSize** 处理，把行高撑到子控件的首选高度（按钮的 66 逻辑像素），
超出父容器后被裁剪，视觉上就是一个虚高的框。

修复：显式声明

```csharp
t2row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
```

### 6. 跨进程读不到 WinForms 控件的 Text

用 `WM_SETTEXT` 从外部给 `TextBox` 赋值后，窗口上显示正常，
但跨进程读取时 `Text` 属性返回空字符串 —— WinForms 在托管侧有一层缓存，
`WM_GETTEXT` 走的是另一条路径。

这是**测试脚本**的问题，不是程序的问题。可靠的自动化方式是：
真实鼠标点击 + 剪贴板 `^v` 粘贴。

### 7. 深色模式没有官方 API

WinForms 不会自动跟随系统深色。可用的是一套未公开接口：

| 接口 | DLL / 序号 | 作用 |
| --- | --- | --- |
| `SetPreferredAppMode` | uxtheme `#135` | 进程级明暗（2 = 强制深色，3 = 强制浅色） |
| `AllowDarkModeForWindow` | uxtheme `#133` | 允许单个窗口使用深色 |
| `SetWindowTheme(hwnd, "DarkMode_Explorer", null)` | uxtheme | 让控件改用深色主题绘制 |
| `DwmSetWindowAttribute(hwnd, 20, &1, 4)` | dwmapi | 标题栏深色（Win10 1809+） |

两个时机决定成败：

- `SetPreferredAppMode` 必须在 `Application.EnableVisualStyles()` 之后、
  **创建任何窗口之前**调用，晚了不生效。
- `SetWindowTheme` 要在控件 Handle 创建后调用（挂 `HandleCreated` 事件）。

系统是否深色读注册表
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` 的
`AppsUseLightTheme`（`0` = 深色）。变化通知是 `WM_SETTINGCHANGE`（`0x001A`）。

这些序号接口在旧系统上不存在，必须 `try/catch` 兜底，否则直接崩。

### 8. TextBox 的滚动条不是 WinForms 画的

多行 `TextBox` 右侧的滚动条由 `uxtheme` 绘制，没有任何托管属性可以改它的颜色，
深色界面下会突兀地留一条白边（实测 `RGB(240,240,240)`）。
只能靠第 7 条里的 `SetWindowTheme` 把它切到深色主题，切完是 `RGB(23,23,23)`。

排查时的坑：`RichTextBox` 的窗口类名是 `RICHEDIT50W`，
用 `*EDIT*` 模糊匹配会把两者都选中，得写成
`-like "*EDIT*" -and -notlike "*RICHEDIT*"` 才能只挑出 TextBox。

### 9. 自绘控件要单独开文字抗锯齿

`Control` 派生的自绘控件如果不设 `TextRenderingHint`，
`Graphics.DrawString` 会走默认渲染，笔画发虚（尤其小字号）。

```csharp
g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
```

`SmoothingMode.AntiAlias` 只影响图形轮廓（圆角、线条），**对文字无效**，两者要分别设。

### 10. `String.Replace("")` 会直接抛异常

`"abc".Replace("", "x")` 在 C# 里会抛 `ArgumentException: 字符串的长度不能为零`；
而 Python 的 `"abc".replace("", "x")` 合法，返回原串。

机型字段取自 `username-item` 第二个 `<p>` 里的 `<span>`，
再用 `full.Replace(device, "")` 从「时间 + 机型」的合并文本里裁掉机型。
遇到**机型 span 是空标签**的文章（例如 `feed/67228350`），`device` 是空串，
这一行就让整篇导出失败。

修法是裁剪前判空：

```csharp
if (device.Length > 0)
    full = full.Replace(device, "").Trim();
```

这个 bug 只在 C# 版存在，Python 版天生不会触发 —— 这是两版独立实现的代价，
凡是拿变量当 `oldValue` 的 `Replace` 调用，都要先想想它可能为空。

---

## 四、图片与文件处理

### 扩展名修正

服务端有时用 `.jpg` 名义返回 PNG 字节。工具读取前几个字节判断真实类型：

| 魔数 | 类型 |
| --- | --- |
| `FF D8 FF` | JPEG |
| `89 50 4E 47` | PNG |
| `47 49 46 38` | GIF |
| `52 49 46 46` | WebP（RIFF 容器，需再看 `WEBP` 标记） |

### 文件名清洗

Windows 非法字符 `\ / : * ? " < > |` 全部替换为 `_`，
连续空白压缩为单个空格，标题截断到 60 字符，
重名时追加 `_2`、`_3`。

### 行首转义

正文里以 `#`、`>`、`|`、`- `、`1.` 开头的行会被 Markdown 解释成标题、引用、表格、列表。
工具对这类行首加反斜杠转义，保持原文视觉。

---

## 五、两版实现的一致性

`Coolapk2MD.cs` 与 `coolapk2md.py` 独立实现，但对同一篇文章的输出**逐字节一致**：

- 相同的 YAML front matter 字段顺序
- 相同的图片编号规则（封面为 01，正文图从 02 起）
- 相同的空行与换行策略
- 相同的转义规则

这是靠对比两个版本的输出文件（`fc /b`）反复修正得到的。

---

## 六、测试方法

界面部分无法靠肉眼判断，采用以下方式验证：

1. **控件树测量**：枚举子窗口，读取每个控件的物理尺寸，换算逻辑像素比对预期
2. **像素采样**：对窗口截图后采样特定坐标
   - 按钮中心应为 `RGB(72,174,76)`
   - 圆角外侧 4px 处应是父容器背景色 `RGB(243,245,248)`，内侧 12px 处是绿色
   - 采样结果同时验证了配色与圆角是否真的绘制
3. **端到端**：真实鼠标点击 + 剪贴板粘贴 + 等待产物落盘，检查 md 与图片数量
4. **命令行回归**：同一链接跑 CLI 模式，与界面输出比对
5. **深色模式**：改注册表 `AppsUseLightTheme` 后广播 `WM_SETTINGCHANGE`
   （`SendMessageTimeout(HWND_BROADCAST, 0x001A, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, ...)`），
   再采样验证。测试脚本必须用 `try/finally` 把注册表改回原值。
   预期值：页面底 `RGB(26,29,33)`、卡片 `RGB(36,40,45)`、主文字 `RGB(230,233,237)`、
   输入框滚动条 `RGB(23,23,23)`；浅色下滚动条应为 `RGB(240,240,240)`。
6. **递归导出回归**：固定用 `feed/72078845` 这一篇，勾选后应得到 9 个目录，
   逐个核对 md 大小与图片张数；Python 版跑 `--with-refs` 后与图形界面产物比对。