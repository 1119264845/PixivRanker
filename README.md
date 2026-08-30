# Pixiv 排行榜下载器

一个面向 Windows 的 Pixiv 排行榜归档工具。它使用 WebView2 完成 Pixiv 登录并复用会话，然后读取排行榜内容、展示作品信息，并按名次批量下载原图或将动图自动转换为 GIF。

项目定位为个人本地归档工具，强调简单、直观和可重复下载：再次下载同一榜单时，会根据作品 ID 自动跳过已经完整保存的作品。

榜单会显示作品的本地状态（未下载、已下载、黑名单或不可下载）。右键单击作品可以打开 Pixiv 详情页、将作者加入或移出下载黑名单，以及删除当前榜单目录中的对应本地图片。作者黑名单会随程序设置持久保存。

## 技术栈

- .NET 8
- WPF + XAML
- C#
- Microsoft WebView2
- `HttpClient` + Pixiv AJAX 接口
- `System.Text.Json`

## 构建

需要 Windows、.NET 8 SDK 和 WebView2 Runtime：

Windows 下可直接双击项目根目录的 `一键重新构建并运行.cmd`，脚本会完成 Release 构建并启动程序。

```powershell
dotnet restore .\PixivRanker.sln
dotnet build .\PixivRanker.sln -c Release
```

输出位于 `src\PixivRanker\bin\Release\net8.0-windows\`。

## 使用方式

1. 启动程序并在内置 Pixiv 页面中登录。
2. 选择排行榜类型、日期范围和作品类型。
3. 获取排行榜后选择要下载的名次或名次区间。
4. 选择保存目录并开始下载。

程序会记住上次使用的保存目录和主题设置。

## 使用提醒

本软件定位为个人本地归档工具。请控制下载频率，不要绕过 Pixiv 的访问限制；作品版权仍归原作者，下载不代表获得转载、分发、商用或训练模型的许可。

## 许可证

本项目采用 [MIT License](LICENSE) 发布。使用本工具下载的 Pixiv 作品仍受原作者及相关平台规则约束。
