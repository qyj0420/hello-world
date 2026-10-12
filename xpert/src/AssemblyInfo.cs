using System.Reflection;
using System.Runtime.InteropServices;

#if PUBLIC_BETA_NO_DRIVER_V112_20261011
[assembly: AssemblyTitle("HV-DAC Xpert V1.1.2")]
#elif V30_BATCH_VALIDATION_20261008
[assembly: AssemblyTitle("HV-DAC Xpert 公测版V1.1.1")]
#else
[assembly: AssemblyTitle("HV-DAC Xpert")]
#endif
[assembly: AssemblyDescription("Cable test data acquisition, analysis and reporting")]
[assembly: AssemblyCompany("")]
#if PUBLIC_BETA_NO_DRIVER_V112_20261011
[assembly: AssemblyProduct("HV-DAC Xpert V1.1.2")]
#elif V30_BATCH_VALIDATION_20261008
[assembly: AssemblyProduct("HV-DAC Xpert 公测版V1.1.1")]
#else
[assembly: AssemblyProduct("HV-DAC Xpert")]
#endif
[assembly: AssemblyCopyright("")]
[assembly: ComVisible(false)]
[assembly: Guid("bb2870ce-892b-4c45-8a5e-d814f03dd904")]
#if PUBLIC_BETA_NO_DRIVER_V112_20261011
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]
[assembly: AssemblyInformationalVersion("V1.1.2 r57; isolated customer no-driver installer; bounded project and report inputs; explicit AI region statistics; Eplus-final-pair-linear-score-ownership-r01; physical acceptance and dependency maintenance pending")]
#elif V30_BATCH_VALIDATION_20261008
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]
#if PUBLIC_BETA_NO_DRIVER_20261011
[assembly: AssemblyInformationalVersion("Public Beta V1.1.1 r53; no-driver customer validation 20261011; Eplus-final-pair-linear-score-ownership-r01; physical acceptance pending")]
#else
[assembly: AssemblyInformationalVersion("公测版V1.1.1 r50; calibration-pressure fixes 20261011; offline verification only, physical acceptance pending")]
#endif
#else
[assembly: AssemblyVersion("1.1.9.0")]
[assembly: AssemblyFileVersion("1.1.9.9")]
#endif
