# HLK 串口台 · HLK Serial Desk

面向 **HLK-7621A / OpenWrt** 的 Windows 串口工具，用于捕获启动日志、查看控制台回显和保存调试记录。

[下载 Windows 版（GitHub Releases）](https://github.com/llc6666/hlk-serial-desk/releases)

## 使用

1. 解压发行包，双击 `HLK-SerialDesk.exe`。使用板载 CH340 时，Windows 需能识别对应串口。
2. 用数据线连接板上的 USB1，选对 COM 口。应用预设 **57600 / 8N1 / 无流控**，也可切换波特率。需要抓完整启动过程时，先点「开始监听」，再给板子上电。
3. 监听默认只接收。需要发送时，确认板端已进入系统，再勾选「启用发送」。手动发送、单个回车和只读预设分别操作。一旦识别到 U-Boot，工具会锁住发送，直到识别到系统初始化结束。
4. 点击「打开会话目录」查看文件。默认位于 Windows 的 **文档 / HLK-SerialDesk / Sessions**；如果没有可用的文档目录，则使用本地应用数据目录。

| 文件 | 用途 |
| --- | --- |
| `rx.txt` | 可直接打开的 UTF-8 文本，过滤终端控制字符并统一换行。 |
| `rx.raw` | 原样接收字节，供复核和重放。 |
| `events.log` | 会话时间、连接参数、发送记录及结束原因。 |
| `SHA256SUMS.txt` | 断开后生成，分别校验以上三份文件。 |

在「离线回放」里打开 [合成演示日志](samples/demo-log.txt) 就可以体验界面，无需板子。日志区按 **Ctrl+F** 查找。会话文件留在本机，不会自动上传。

## 构建与离线检查

在 Windows PowerShell 中执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\build.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-Capture.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned -File .\tests\Test-Send.ps1
```

构建使用 Windows 的 .NET Framework C# 编译器，产物为 `build/HLK-SerialDesk.exe`。两个测试使用合成输入，覆盖接收文件、原始字节与校验，以及发送成功、取消、写入失败和 U-Boot 锁定；不会向实体串口写数据。

HLK Serial Desk is a Windows serial console for HLK-7621A/OpenWrt boot capture, manual troubleshooting, and offline log replay. Sessions retain both a readable UTF-8 transcript and the original received bytes.

[MIT License](LICENSE)
