package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.Project

object Project : Project({
    description = "Cross-platform Vivarium compile, release, and publish pipeline"

    vcsRoot(VivariumVcs)

    buildType(CompileWindowsX64)
    buildType(CompileLinuxX64)
    buildType(CompileLinuxArm64)
    buildType(CompileMacosArm64)
    buildType(Release)
    buildType(Publish)

    buildTypesOrder = arrayListOf(
        CompileWindowsX64,
        CompileLinuxX64,
        CompileLinuxArm64,
        CompileMacosArm64,
        Release,
        Publish,
    )
})
