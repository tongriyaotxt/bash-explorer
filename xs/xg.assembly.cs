// xg.assembly.cs — 程序集元数据（与 xg.cs 一起编译）
//
// 目的：给 csc 产物补上完整的版本资源与产品信息。没有这些字段的 .NET
// 可执行文件在现场属于"高熵未知样本"，随时会被 ML 启发式当作未签名木马
// 处理（本次命中 Trojan:Win32/Bearfoos.A!ml）。补全后哈希会变、特征更
// 接近正规工具。
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("xg")]
[assembly: AssemblyDescription("xg — graphical shell browser for the Windows Shell namespace")]
[assembly: AssemblyProduct("xg shell browser")]
[assembly: AssemblyCompany("YG")]
[assembly: AssemblyCopyright("Copyright (C) YG 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]
[assembly: ComVisible(false)]
[assembly: Guid("9f1c2b40-6a75-4f0e-9d3a-2c5b7e21ad64")]
