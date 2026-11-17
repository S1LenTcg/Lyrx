# InfLink-rs 3.4.0

InfLink-rs 是随 Lyrx Windows 发布包一起提供的独立 BetterNCM 插件，由 apoint123 发布，采用 GPL-3.0-only。Lyrx 在用户选择安装后复制原始插件文件。 [上游发布页](https://github.com/apoint123/inflink-rs/releases/tag/v3.4.0)

| 文件 | 来源 |
| --- | --- |
| `InfLink-rs.plugin` | 上游 `v3.4.0` 的原始发布附件 |
| `InfLink-rs-v3.4.0-source.zip` | 插件源码、依赖锁定文件和构建脚本，对应提交 `e4ad4946ce24f63b99082fe8be3f30ca92d4428e` |
| `InfLink-rs-libcef-615be4a613a63225aade97ba287cb3f343bff797.zip` | 上游源码引用的 `libs/libcef` 子模块，对应提交 `615be4a613a63225aade97ba287cb3f343bff797` |
| `LICENSE` | 插件的 GPL 3.0 许可全文 |

若要重建插件，先解压插件源码，再把 libcef 源码压缩包顶层目录中的内容放入插件源码的 `libs/libcef/`。然后按照插件源码里的 README 执行构建步骤。 [上游源码提交](https://github.com/apoint123/inflink-rs/commit/e4ad4946ce24f63b99082fe8be3f30ca92d4428e) · [libcef 子模块提交](https://github.com/BetterNCM/libcef/commit/615be4a613a63225aade97ba287cb3f343bff797)

原始插件文件 SHA-256 为 `7d0b7b863b072a99b88eb614c788099315e690920889aaca22685d3663661468`。
