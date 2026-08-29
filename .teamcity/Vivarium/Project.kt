package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.Project

object Project : Project({
    description = "Cross-platform Vivarium verification and guarded release candidate pipeline"

    vcsRoot(VivariumVcs)

    buildType(VerifyWindows)
    buildType(VerifyLinux)
    buildType(VerifyMacos)
    buildType(CiGate)
    buildType(ReleasePackage)
    buildType(ReleaseSmokeWindows)
    buildType(ReleaseSmokeLinux)
    buildType(ReleaseSmokeLinuxArm64)
    buildType(ReleaseSmokeMacos)
    buildType(ReleaseGate)
    buildType(PublishGitHub)

    buildTypesOrder = arrayListOf(
        VerifyWindows,
        VerifyLinux,
        VerifyMacos,
        CiGate,
        ReleasePackage,
        ReleaseSmokeWindows,
        ReleaseSmokeLinux,
        ReleaseSmokeLinuxArm64,
        ReleaseSmokeMacos,
        ReleaseGate,
        PublishGitHub,
    )
})
