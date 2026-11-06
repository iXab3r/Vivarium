@business @operations @data-provider
Feature: Operators choose storage without changing the product
  Storage selection changes deployment requirements, not build, access or automation behavior.
  All projects, accounts, agents, source repositories and publication targets are synthetic.
  These criteria are specified-unbound; their presence is not execution or compatibility evidence.

  @AC-DP-01
  Scenario Outline: Choose a storage provider for a new installation
    Given the installation uses <provider> as its storage provider
    And an operator has prepared the supported resources for a new installation
    When the operator initializes it using the selected storage provider
    Then the installation identifies its selected provider and supports its ordinary administration
    And no alternative installation or provider is selected silently

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-02
  Scenario Outline: Complete a headless build journey
    Given the installation uses <provider> as its storage provider
    And an ordinary user can manage a synthetic project and an authorized compatible agent is ready
    When the user configures and runs a small build through supported APIs and CLI
    Then the user can inspect its result and retrieve its exact log and artifact bytes
    And the journey needs no Vivarium web interface or TeamCity execution server

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-03
  Scenario Outline: Apply current rights to existing credentials
    Given the installation uses <provider> as its storage provider
    And an ordinary user has a working credential and permission to start builds
    When an administrator removes the last applicable grant and the user submits another build after that change completes
    Then the request is denied without admitting work
    And an authorized administrator can still inspect the earlier history and its actor attribution

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-04
  Scenario Outline: Preserve distinct simultaneous build requests
    Given the installation uses <provider> as its storage provider
    And two authorized users deliberately request separate builds of the same configuration
    When their requests arrive concurrently
    Then each accepted request has a distinct build identity and the expected configuration build number
    And neither request replaces the other or creates an unintended extra execution

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-05
  Scenario Outline: Use an enrollment invitation only as allowed
    Given the installation uses <provider> as its storage provider
    And a synthetic enrollment invitation has the declared single-use contract
    When two agents attempt to claim it concurrently
    Then only the permitted enrollment succeeds
    And the rejected attempt cannot assume the admitted agent identity

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-06
  Scenario Outline: Consume prepared run input once
    Given the installation uses <provider> as its storage provider
    And an ordinary user has prepared one permitted temporary chain input
    When two build requests attempt to consume that same input concurrently
    Then at most one request consumes the input under its declared contract
    And the secret is absent from public responses and logs

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-07
  Scenario Outline: Recover acknowledged work after restart
    Given the installation uses <provider> as its storage provider
    And the server has acknowledged queued work and received output from a running build
    When the server stops unexpectedly and the operator restarts the same installation
    Then acknowledged work and output remain accounted for with truthful recovery states
    And recovery does not silently create a second execution

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-08
  Scenario Outline: Reject results from a superseded agent session
    Given the installation uses <provider> as its storage provider
    And a newer agent session has replaced an earlier session
    When the earlier session reports a result for work it no longer owns
    Then the report cannot overwrite the current result or release the current owner
    And the operator can still determine the actual build and agent state

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-09
  Scenario Outline: Protect an artifact while it is being uploaded
    Given the installation uses <provider> as its storage provider
    And an admitted build is uploading a declared artifact
    When cleanup runs concurrently with the upload
    Then cleanup cannot remove the artifact while that upload still owns it
    And only verified complete bytes can become an available artifact

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-10
  Scenario Outline: Recover an interrupted upload and cleanup
    Given the installation uses <provider> as its storage provider
    And an upload or cleanup operation has been interrupted
    When the installation restarts and the operator requests retained and expired artifacts
    Then retained artifacts remain readable and unfinished cleanup can resume
    And incomplete or expired content is not presented as a complete successful download

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-11
  Scenario Outline: Preserve filtering and pagination
    Given the installation uses <provider> as its storage provider
    And a synthetic history contains case-sensitive identities, equal timestamps and precisely ordered output ranges
    When an authorized user filters and pages through the supported history and log APIs
    Then the user receives the expected visible records in the declared stable order
    And records are not lost, duplicated or rounded into another identity

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-12
  Scenario Outline: Preserve source and trigger ownership
    Given the installation uses <provider> as its storage provider
    And a synthetic configuration has versioned settings and an eligible source change
    When configuration reconciliation and trigger processing overlap with a restart
    Then the resulting builds retain the accepted source and settings identities
    And the change is not silently lost or published twice because of recovery

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-13
  Scenario Outline: Upgrade stored data without losing history
    Given the installation uses <provider> as its storage provider
    And an installation contains data written by a supported earlier release
    When the operator performs the supported upgrade and restarts it
    Then accounts, grants, agent identities and retained build evidence remain usable
    And an interrupted upgrade can only resume under its declared recovery procedure

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-14
  Scenario Outline: Reject incompatible or damaged stored data
    Given the installation uses <provider> as its storage provider
    And an installation has damaged data or a schema newer than the running server supports
    When the operator attempts to start it
    Then the affected installation remains unavailable with an actionable diagnostic
    And the server neither dispatches work nor substitutes a new empty installation

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-15
  Scenario Outline: Restore a complete installation
    Given the installation uses <provider> as its storage provider
    And an operator has a complete supported backup containing synthetic identity, settings and build evidence
    When the operator restores it into an isolated target using the same provider
    Then current permissions and retained build identities, logs and artifact bytes are coherent
    And a backup missing required keys or bytes cannot be declared a successful restore

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-16
  Scenario Outline: Reject a provider change that would abandon existing data
    Given the installation uses <provider> as its storage provider
    And an installation contains retained work using its selected provider
    When an operator changes the provider setting without performing a supported data transfer
    Then the installation refuses to treat the change as a completed migration
    And existing history is preserved and no empty replacement is activated silently

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-17
  Scenario Outline: Handle temporary storage contention
    Given the installation uses <provider> as its storage provider
    And an authorized operation encounters temporary contention while other work is active
    When the contention clears within the supported deadline or persists beyond it
    Then the operation either completes under its declared contract or reports the applicable failure
    And the server does not report success for an uncommitted change or duplicate external effects

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-18
  Scenario Outline: Report unavailable storage without inventing success
    Given the installation uses <provider> as its storage provider
    And a build or access change is in progress
    When storage becomes unavailable or cannot accept further writes
    Then the caller receives only outcomes supported by durable evidence
    And after recovery the operator can distinguish committed work from failed or unresolved work

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-19
  Scenario Outline: Keep independent installations isolated
    Given the installation uses <provider> as its storage provider
    And two synthetic installations have distinct storage resources and credentials
    When their operators start and use them concurrently
    Then each installation exposes only its own identities, work and retained bytes
    And an unsupported attempt to share exclusive storage ownership is refused without damaging the owner

    Examples:
      | provider   |
      | PostgreSQL |
      | SQLite     |

  @AC-DP-20
  Scenario: Operate with SQLite without provisioning PostgreSQL
    Given an operator has prepared the non-database prerequisites and has no PostgreSQL service
    When the operator initializes a SQLite installation and runs a small build with a separate real agent
    Then the ordinary headless journey and restart retention succeed
    And no PostgreSQL service or hidden PostgreSQL connection is required
