package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.Project

object Project : Project({
    description = "Cross-platform Vivarium compile, release, and publish pipeline"

    vcsRoot(VivariumVcs)

    buildType(Compile)
    buildType(Release)
    buildType(PublishGitHub)
    buildType(PublishDocker)

    buildTypesOrder = arrayListOf(
        Compile,
        Release,
        PublishGitHub,
        PublishDocker,
    )
})
