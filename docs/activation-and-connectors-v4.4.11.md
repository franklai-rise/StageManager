# v4.4.11 窗口激活与连接线修复

- VPN Manager 的 NotifyIcon 只处理 DoubleClick，InvokePattern 成功不等于窗口成功恢复。使用其现有 Local\VpnManager.Activate 事件唤醒原实例，无网络状态修改。
- Clash 点击通过单个隔离工作进程读取当前通知区名称并调用，避免缓存名称、提前返回和最小化标志造成跳过。成功调用后不再次操作托盘展开按钮，防止抢回前台。
- IsForegroundForWindow 必须同时满足窗口存在、可见、未最小化，再验证前台所属关系。
- 连接线过去未进入 native window Region，卡片之间的空隙会裁剪线条。现将连接线完整投影加入只显示、不拦截点击的区域。未改连接线颜色、尺寸或添加动画。

验证：67 项回归通过，包含连接线全长 19 个采样点的显示区域检查。另以 --live-activation-probe 调用 PrototypeForm 中卡片共用的 ActivateSelectedWindow，实测 Clash 与 VPN Manager 均 foreground=True、minimized=False。该测试不包含真实鼠标命中或视觉截图，不能替代完整手动体验确认。

本机固定入口已更新到 v4.4.11；保留 v4.4.10 回退副本。未发布 GitHub Release。
