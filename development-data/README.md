# 可复现开发样例

本目录只使用合成数据，不包含真实业务产品、用户素材或凭据。

运行 `create-sample-data.ps1` 会生成：

- `sample-product-diagram.png`：脚本生成的测试占位图。
- `sample-models/`：脚本生成的最小有效 3MF 样例。

```powershell
.\development-data\create-sample-data.ps1
```

然后在 PartMap 中新建产品：

- 产品名：`示例产品`
- 包装图：`development-data\sample-product-diagram.png`
- 模型目录：`development-data\sample-models`

根目录有普通 3MF 与 `.gcode(1).3mf`；`批次A` 用于验证“读取子目录”；`历史` 中的模型必须始终不显示。