package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.Project

object Project : Project({
    description = "Cross-platform Vivarium compile, release, and publish pipeline"

    params {
        password("github.release.token", "credentialsJSON:3037329c-567e-43e4-a236-0d2052a38d00")
    }

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
