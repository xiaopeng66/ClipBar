# REVIEW-1.2 基线（2026-11-06）

- HEAD：`a2ec088`（工作树干净）
- 变更面：v1.1.0..HEAD 12 提交，39 文件 +2402/−633
- 测试基线：App 243 + Core 204 = 447 全绿（`dotnet test ClipBar.slnx -c Release`）
- 门禁基线：`check-encoding.py` OK；`scan-path-violations.py` 干净；`build.cmd` 双产物成功
- 产物：dist\ClipBar.exe（自包含）、dist-lite\ClipBar.exe（框架依赖），与 HEAD 同源
- 已知未决（上轮遗留）：设置"应用后写盘失败"保持生效+警告（未回滚）；UIA 焦点项断言未自动化
