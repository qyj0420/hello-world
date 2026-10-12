# HV-DAC Xpert V1.1.2 - r58 Analysis Hotfix source snapshot

This is a source-only handoff of the owned r58 analysis hotfix working tree.
It includes C# desktop/review source, owned Python research adapter source,
the existing owned C++ hardware boundary source, tests and whitelisted build
scripts/resources. The hardware remains the existing r58 implementation;
this snapshot does not claim that the separate Grok hardware path is integrated.

This source snapshot is independent of any EXE or installer. No EXE hash,
installer binding, confidentiality/encryption completion, all-algorithm
completeness, hardware execution or physical acceptance is claimed.

SOURCE_INDEX.json contains the SHA256 and byte length of every included source
file. SNAPSHOT_BINDING.json identifies this source-only artifact. The exporter
also verified every ZIP entry, including these metadata documents, and checked
that the source inventory stayed unchanged while copying.

The build scripts require separately provisioned, approved runtime dependencies,
the existing frozen build inputs and local compiler/protection tools. These are
not included. Exact Task12 proprietary source is unavailable and is not included.
The exported tree is not a standalone dependency bundle. Historical build paths
in existing scripts are not automatically portable to another computer.

Do not copy personal or installation credentials into the source tree. No user
projects, LOC/CAL data, drivers, executable/DLL/PYD files, models, build outputs,
runtime caches or toolchain executables are part of this archive. Selection uses
a reviewed path/extension whitelist; it is not a content-level secret audit.

本压缩包仅用于本次 r58 分析热修复源码交接，包含白名单源码、测试、构建脚本与必要资源。
它不代表最终 EXE/安装包与源码已经绑定，也不代表全部闭源算法或独立构建依赖齐全。
未操作产品、驱动 SDK 或实机；旧发行包和用户项目均未修改。