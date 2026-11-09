# ez2lazer.Veldrid

Ez2Lazer 维护的 [ppy/veldrid](https://github.com/ppy/veldrid) fork，供 [ez2lazer-framework](https://github.com/SK-la/ez2lazer-framework) 使用（10-bit / HDR 等 DXGI 扩展在此仓库演进）。

Veldrid 是面向 .NET 的跨平台图形与计算库，后端含 Vulkan、Metal、Direct3D 11、OpenGL 等。

## NuGet 包

| PackageId | 说明 |
|-----------|------|
| `ez2lazer.Veldrid` | 主库（内含 OpenGL / Metal 绑定程序集） |

程序集名仍为 `ppy.Veldrid*` ；NuGet 仅发布 **一个** 包，Trusted Publishing 只需配置 `ez2lazer.Veldrid`。

## 版本号

发布使用 **`yyyy.mdd.patch`**，无预发布后缀，例如 `2026.1109.0`（2026 年 11 月 9 日第 0 次修订）。同日再发用 `2026.1109.1`。

Git tag 可与 NuGet 版本相同，或带可选前缀 `v`（如 `v2026.1109.0`）；CI 会去掉 `v` 再写入包版本。

## 发布（维护者）

1. 在 [nuget.org](https://www.nuget.org) 为 **`ez2lazer.Veldrid`** 配置 **Trusted Publishing**（GitHub 仓库 `SK-la/ez2lazer-veldrid`，工作流 `deploy-pack.yml`）。
2. 打 tag 并推送：
   ```bash
   git tag 2026.1109.0
   git push origin 2026.1109.0
   ```
3. 或于 GitHub Actions 手动运行 **Pack and publish NuGet**，填写版本；若只想发到 GitHub Packages，将 `publish_target` 选为 `github`。

推送 tag 时默认发布到 **nuget.org**（OIDC，用户 `SK_LA`）。手动 workflow 可选 **github** → `https://nuget.pkg.github.com/SK-la/index.json`。

## 本地开发与 framework 联调

在 `ez2lazer-framework` 根目录执行：

```powershell
.\UseLocalVeldrid.ps1
```

要求本仓库与 framework 为兄弟目录（`../ez2lazer-veldrid`）。脚本会改为 `ProjectReference` 并加入 solution filter。

恢复 NuGet 包引用：

```powershell
dotnet add osu.Framework/osu.Framework.csproj package ez2lazer.Veldrid --version <版本>
```

## 许可证

MIT（见 [LICENSE](LICENSE)）。
