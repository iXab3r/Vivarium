using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Catalog;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildModel
{
    internal static void Configure(ModelBuilder model)
    {
        var build = model.Entity<RuntimeBuild>();
        build.HasKey(b => b.Id); build.Property(b => b.Id).UseIdentityAlwaysColumn();
        build.HasAlternateKey(b => new { b.Id, b.BuildTypeId, b.ProjectId });
        build.HasAlternateKey(b => new { b.Id, b.BuildTypeId });
        build.HasIndex(b => new { b.BuildTypeId, b.Counter }).IsUnique();
        build.HasOne<CatalogBuildType>().WithMany().HasForeignKey(b => new { b.BuildTypeId, b.ProjectId })
            .HasPrincipalKey(t => new { t.Id, t.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        // Historical actor identity survives ordinary account deletion; it is not a live grant FK.
        build.HasIndex(b => new { b.State, b.QueuedAt, b.Id });
        build.ToTable("RuntimeBuilds", table =>
        {
            table.HasCheckConstraint("CK_RuntimeBuilds_Id", "\"Id\" > 0 AND (\"Counter\" IS NULL OR \"Counter\" > 0)");
            table.HasCheckConstraint("CK_RuntimeBuilds_State", "\"State\" IN ('QUEUED','RUNNING','RELEASING','FINISHED')");
            table.HasCheckConstraint("CK_RuntimeBuilds_Status", "\"Status\" IN ('UNKNOWN','SUCCESS','FAILURE')");
            table.HasCheckConstraint("CK_RuntimeBuilds_Stop", "\"StopMode\" IS NULL OR \"StopMode\" IN ('GRACEFUL','FORCE')");
        });
        var counter = model.Entity<RuntimeBuildCounter>(); counter.HasKey(c => c.BuildTypeId);
        counter.HasOne<CatalogBuildType>().WithOne().HasForeignKey<RuntimeBuildCounter>(c => c.BuildTypeId).OnDelete(DeleteBehavior.Restrict);
        counter.ToTable("RuntimeBuildCounters", t => t.HasCheckConstraint("CK_RuntimeBuildCounters_Positive", "\"NextValue\" > 0"));

        var runInput = model.Entity<RuntimeBuildRunInput>(); runInput.HasKey(i => i.Id);
        runInput.HasIndex(i => new { i.ActorId, i.State, i.ExpiresAt });
        runInput.HasOne<CatalogBuildType>().WithMany().HasForeignKey(i => new { i.BuildTypeId, i.ProjectId })
            .HasPrincipalKey(t => new { t.Id, t.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        runInput.HasOne<CatalogRevision>().WithMany().HasForeignKey(i => i.RevisionId).OnDelete(DeleteBehavior.Restrict);
        runInput.HasOne<RuntimeBuild>().WithMany().HasForeignKey(i => new { i.AcceptedBuildId, i.BuildTypeId, i.ProjectId })
            .HasPrincipalKey(b => new { b.Id, b.BuildTypeId, b.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        runInput.ToTable("RuntimeBuildRunInputs", t =>
        {
            t.HasCheckConstraint("CK_RuntimeBuildRunInputs_Lifetime", "\"ExpiresAt\" > \"CreatedAt\"");
            t.HasCheckConstraint("CK_RuntimeBuildRunInputs_State", "(\"State\" = 'PENDING' AND octet_length(\"ProtectedInputs\") > 0 AND \"AcceptedBuildId\" IS NULL) OR (\"State\" = 'CONSUMED' AND octet_length(\"ProtectedInputs\") = 0 AND \"AcceptedBuildId\" IS NOT NULL) OR (\"State\" = 'EXPIRED' AND octet_length(\"ProtectedInputs\") = 0 AND \"AcceptedBuildId\" IS NULL)");
        });

        var input = model.Entity<RuntimeBuildInput>(); input.HasKey(i => i.BuildId);
        input.HasOne<RuntimeBuild>().WithOne().HasForeignKey<RuntimeBuildInput>(i => new { i.BuildId, i.BuildTypeId, i.ProjectId })
            .HasPrincipalKey<RuntimeBuild>(b => new { b.Id, b.BuildTypeId, b.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        input.HasOne<CatalogBuildDefinition>().WithMany().HasForeignKey(i => new { i.BuildTypeId, i.RevisionId }).OnDelete(DeleteBehavior.Restrict);
        input.Property(i => i.OverridesJson).HasColumnType("jsonb"); input.Property(i => i.AdmissionJson).HasColumnType("jsonb");
        input.Property(i => i.SourceJson).HasColumnType("jsonb");
        input.HasOne<RuntimeVcsRootInstance>().WithMany().HasForeignKey(i => new { i.VcsRootInstanceId, i.ProjectId })
            .HasPrincipalKey(r => new { r.Id, r.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        input.ToTable("RuntimeBuildInputs", t => t.HasCheckConstraint("CK_RuntimeBuildInputs_Source",
            "(\"VcsRootInstanceId\" IS NULL) = (\"SourceJson\" IS NULL)"));

        var root = model.Entity<RuntimeVcsRootInstance>();
        root.HasKey(r => r.Id); root.Property(r => r.Id).UseIdentityAlwaysColumn();
        root.HasAlternateKey(r => new { r.Id, r.ProjectId });
        root.HasIndex(r => new { r.ProjectId, r.RootId, r.PropertiesHash }).IsUnique();
        root.HasOne<CatalogProjectIdentity>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
        root.Property(r => r.PropertiesJson).HasColumnType("jsonb");
        root.ToTable("RuntimeVcsRootInstances", t => t.HasCheckConstraint("CK_RuntimeVcsRootInstances_Identity",
            "\"Id\" > 0 AND length(\"RootId\") BETWEEN 1 AND 256 AND \"PropertiesHash\" ~ '^[a-f0-9]{64}$'"));
        var selection = model.Entity<RuntimeBuildSourceSelection>(); selection.HasKey(s => s.BuildId);
        selection.HasOne<RuntimeBuildInput>().WithOne().HasForeignKey<RuntimeBuildSourceSelection>(s => s.BuildId).OnDelete(DeleteBehavior.Restrict);
        selection.ToTable("RuntimeBuildSourceSelections", t => t.HasCheckConstraint("CK_RuntimeBuildSourceSelections_Version",
            "\"Version\" = 'N/A' OR \"Version\" ~ '^[a-f0-9]{40}$'"));
        var receipt = model.Entity<RuntimeBuildSourceReceipt>(); receipt.HasKey(s => s.BuildId);
        receipt.HasOne<RuntimeBuildExecution>().WithOne().HasForeignKey<RuntimeBuildSourceReceipt>(s => s.BuildId).OnDelete(DeleteBehavior.Restrict);
        receipt.ToTable("RuntimeBuildSourceReceipts");

        var resolution = model.Entity<RuntimeBuildResolution>();
        resolution.HasKey(r => r.RequestedBuildId); resolution.Property(r => r.RequestedBuildId).ValueGeneratedNever();
        resolution.HasOne<RuntimeBuild>().WithMany().HasForeignKey(r => new { r.RequestedBuildId, r.BuildTypeId, r.ProjectId })
            .HasPrincipalKey(b => new { b.Id, b.BuildTypeId, b.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        resolution.HasOne<RuntimeBuild>().WithMany().HasForeignKey(r => new { r.ResolvedBuildId, r.BuildTypeId, r.ProjectId })
            .HasPrincipalKey(b => new { b.Id, b.BuildTypeId, b.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        resolution.ToTable("RuntimeBuildResolutions", t => t.HasCheckConstraint("CK_RuntimeBuildResolutions_NotSelf",
            "\"RequestedBuildId\" <> \"ResolvedBuildId\" AND \"UpdatedAt\" >= \"CreatedAt\""));

        var dependency = model.Entity<RuntimeBuildDependency>();
        dependency.HasKey(d => new { d.BuildId, d.Kind, d.UpstreamBuildTypeId });
        dependency.HasOne<RuntimeBuild>().WithMany().HasForeignKey(d => d.BuildId).OnDelete(DeleteBehavior.Restrict);
        dependency.HasOne<RuntimeBuild>().WithMany().HasForeignKey(d => new { d.UpstreamBuildId, d.UpstreamBuildTypeId })
            .HasPrincipalKey(b => new { b.Id, b.BuildTypeId }).OnDelete(DeleteBehavior.Restrict);
        dependency.ToTable("RuntimeBuildDependencies", t =>
        {
            t.HasCheckConstraint("CK_RuntimeBuildDependencies_Kind", "\"Kind\" IN ('SNAPSHOT','ARTIFACT')");
            t.HasCheckConstraint("CK_RuntimeBuildDependencies_NotSelf", "\"BuildId\" <> \"UpstreamBuildId\"");
        });
        var consumed = model.Entity<RuntimeBuildConsumedArtifact>();
        consumed.HasKey(c => new { c.BuildId, c.Ordinal });
        consumed.HasIndex(c => new { c.BuildId, c.DestinationPath }).IsUnique();
        consumed.HasOne<RuntimeBuild>().WithMany().HasForeignKey(c => c.BuildId).OnDelete(DeleteBehavior.Restrict);
        consumed.HasOne<RuntimeBuildArtifact>().WithMany().HasForeignKey(c => new { c.ProducerBuildId, c.ProducerArtifactOrdinal })
            .HasPrincipalKey(a => new { a.BuildId, a.Ordinal }).OnDelete(DeleteBehavior.Restrict);
        consumed.ToTable("RuntimeBuildConsumedArtifacts", t =>
            t.HasCheckConstraint("CK_RuntimeBuildConsumedArtifacts_Position", "\"Ordinal\" BETWEEN 0 AND 127 AND length(\"DestinationPath\") BETWEEN 1 AND 2048"));

        var execution = model.Entity<RuntimeBuildExecution>(); execution.HasKey(e => e.BuildId);
        execution.HasOne<RuntimeBuild>().WithOne().HasForeignKey<RuntimeBuildExecution>(e => e.BuildId).OnDelete(DeleteBehavior.Restrict);
        execution.HasOne<AgentRegistration>().WithMany().HasForeignKey(e => e.AgentId).OnDelete(DeleteBehavior.Restrict);
        execution.HasIndex(e => e.WorkloadId).IsUnique();
        execution.HasIndex(e => e.AgentId).IsUnique().HasFilter("\"ReleasedAt\" IS NULL");
        execution.Property(e => e.TargetSnapshotJson).HasColumnType("jsonb");
        execution.Property(e => e.SelectionSnapshotJson).HasColumnType("jsonb");
        execution.ToTable("RuntimeBuildExecutions", t =>
        {
            t.HasCheckConstraint("CK_RuntimeBuildExecutions_Fences", "\"LeaseFence\" > 0 AND \"CredentialGeneration\" > 0 AND \"AssignedConnectionGeneration\" > 0 AND \"CurrentConnectionGeneration\" > 0");
            t.HasCheckConstraint("CK_RuntimeBuildExecutions_Phase", "\"Phase\" IN ('PREPARING','DISPATCHING','ACCEPTED','TERMINAL','RELEASED')");
        });
        var observation = model.Entity<RuntimeBuildObservation>();
        observation.HasKey(o => new { o.BuildId, o.Ordinal });
        observation.HasIndex(o => new { o.BuildId, o.Phase, o.StepIndex }).IsUnique();
        observation.HasOne<RuntimeBuildExecution>().WithMany().HasForeignKey(o => o.BuildId).OnDelete(DeleteBehavior.Restrict);
        observation.ToTable("RuntimeBuildObservations", t =>
        {
            t.HasCheckConstraint("CK_RuntimeBuildObservations_Position", "\"Ordinal\" BETWEEN 0 AND 257 AND \"StepIndex\" BETWEEN -1 AND 127");
            t.HasCheckConstraint("CK_RuntimeBuildObservations_Phase", "(\"Phase\" IN ('FETCHING','COLLECTING') AND \"StepIndex\" = -1) OR (\"Phase\" IN ('RUNNING','COMPLETED') AND \"StepIndex\" >= 0)");
            t.HasCheckConstraint("CK_RuntimeBuildObservations_Result", "(\"Phase\" = 'COMPLETED' AND \"ExitCode\" IS NOT NULL AND \"TimedOut\" IS NOT NULL AND \"Skipped\" IS NOT NULL) OR (\"Phase\" <> 'COMPLETED' AND \"ExitCode\" IS NULL AND \"TimedOut\" IS NULL AND \"Skipped\" IS NULL)");
        });
        var result = model.Entity<RuntimeBuildResult>(); result.HasKey(r => r.BuildId);
        result.HasOne<RuntimeBuild>().WithOne().HasForeignKey<RuntimeBuildResult>(r => r.BuildId).OnDelete(DeleteBehavior.Restrict);
        result.ToTable("RuntimeBuildResults", t => t.HasCheckConstraint("CK_RuntimeBuildResults_Hash", "\"SemanticHash\" ~ '^[a-f0-9]{64}$'"));

        var payload = model.Entity<RuntimeBuildPayload>(); payload.HasKey(p => new { p.BuildId, p.Sha256 });
        payload.HasOne<RuntimeBuild>().WithMany().HasForeignKey(p => p.BuildId).OnDelete(DeleteBehavior.Restrict);
        payload.ToTable("RuntimeBuildPayloads", t => t.HasCheckConstraint("CK_RuntimeBuildPayloads_Content", "\"Size\" >= 0 AND \"Sha256\" ~ '^[a-f0-9]{64}$'"));
        var upload = model.Entity<RuntimeArtifactUpload>(); upload.HasKey(u => new { u.BuildId, u.Sha256 });
        upload.HasOne<RuntimeBuild>().WithMany().HasForeignKey(u => u.BuildId).OnDelete(DeleteBehavior.Restrict);
        upload.ToTable("RuntimeArtifactUploads", t => t.HasCheckConstraint("CK_RuntimeArtifactUploads_Content", "\"Size\" >= 0 AND \"Sha256\" ~ '^[a-f0-9]{64}$'"));
        var artifact = model.Entity<RuntimeBuildArtifact>(); artifact.HasKey(a => new { a.BuildId, a.Ordinal });
        artifact.HasIndex(a => new { a.BuildId, a.Path }).IsUnique();
        artifact.HasOne<RuntimeArtifactUpload>().WithMany().HasForeignKey(a => new { a.BuildId, a.Sha256 }).OnDelete(DeleteBehavior.Restrict);
        artifact.ToTable("RuntimeBuildArtifacts", t => t.HasCheckConstraint("CK_RuntimeBuildArtifacts_Content", "\"Ordinal\" >= 0 AND \"Size\" >= 0"));

        var output = model.Entity<RuntimeBuildOutput>(); output.HasKey(o => o.BuildId);
        output.HasOne<RuntimeBuild>().WithOne().HasForeignKey<RuntimeBuildOutput>(o => o.BuildId).OnDelete(DeleteBehavior.Restrict);
        output.ToTable("RuntimeBuildOutputs", t => t.HasCheckConstraint("CK_RuntimeBuildOutputs_Retention", "\"RetentionEpoch\" >= 0"));
        output.ToTable("RuntimeBuildOutputs", t => t.HasCheckConstraint("CK_RuntimeBuildOutputs_State",
            "\"State\" IN ('OPEN','SEALING','COMPLETE','COMPLETE_WITH_GAPS') AND \"NextPosition\" > 0"));
        var stream = model.Entity<RuntimeOutputStream>(); stream.HasKey(s => new { s.BuildId, s.StepIndex, s.Stream });
        stream.HasOne<RuntimeBuildOutput>().WithMany().HasForeignKey(s => s.BuildId).OnDelete(DeleteBehavior.Restrict);
        stream.Property(s => s.CommittedSequence).HasColumnType("numeric(20,0)");
        stream.Property(s => s.CommittedBytes).HasColumnType("numeric(20,0)");
        stream.Property(s => s.FinalSequence).HasColumnType("numeric(20,0)");
        stream.Property(s => s.FinalBytes).HasColumnType("numeric(20,0)");
        stream.ToTable("RuntimeOutputStreams", t =>
        {
            t.HasCheckConstraint("CK_RuntimeOutputStreams_Scope", "\"StepIndex\" >= 0 AND \"Stream\" IN (0,1)");
            t.HasCheckConstraint("CK_RuntimeOutputStreams_Prefix", "\"CommittedSequence\" BETWEEN 0 AND 18446744073709551615 AND \"CommittedBytes\" BETWEEN 0 AND 18446744073709551615");
            t.HasCheckConstraint("CK_RuntimeOutputStreams_Final", "(\"FinalSequence\" IS NULL AND \"FinalBytes\" IS NULL) OR (\"FinalSequence\" IS NOT NULL AND \"FinalBytes\" IS NOT NULL AND \"FinalSequence\" BETWEEN 0 AND 18446744073709551615 AND \"FinalBytes\" BETWEEN 0 AND 18446744073709551615)");
        });
        var range = model.Entity<RuntimeOutputRange>(); range.HasKey(r => r.Id);
        var expiry = model.Entity<RuntimeOutputExpiry>(); expiry.HasKey(e => e.RangeId);
        expiry.HasOne<RuntimeOutputRange>().WithOne().HasForeignKey<RuntimeOutputExpiry>(e => e.RangeId).OnDelete(DeleteBehavior.Restrict);
        expiry.HasIndex(e => e.DeletedAt).HasFilter("\"DeletedAt\" IS NULL");
        expiry.ToTable("RuntimeOutputExpiries", t => t.HasCheckConstraint("CK_RuntimeOutputExpiries_Deletion", "\"DeletedAt\" IS NULL OR \"DeletedAt\" >= \"ExpiredAt\""));
        var deletion = model.Entity<RuntimeOutputDeletion>(); deletion.HasKey(d => d.ObjectKey);
        deletion.HasOne<RuntimeBuild>().WithMany().HasForeignKey(d => d.BuildId).OnDelete(DeleteBehavior.Restrict);
        deletion.HasIndex(d => d.CreatedAt);
        deletion.ToTable("RuntimeOutputDeletions", t => t.HasCheckConstraint("CK_RuntimeOutputDeletions_Key",
            "\"ObjectKey\" ~ '^build-output/[a-f0-9]{32}$' OR \"ObjectKey\" ~ ('^build-artifacts/' || \"BuildId\"::text || '/[a-f0-9]{64}$')"));
        range.HasOne<RuntimeOutputStream>().WithMany().HasForeignKey(r => new { r.BuildId, r.StepIndex, r.Stream }).OnDelete(DeleteBehavior.Restrict);
        range.Property(r => r.Start).HasColumnType("numeric(20,0)"); range.Property(r => r.End).HasColumnType("numeric(20,0)");
        range.Property(r => r.ByteStart).HasColumnType("numeric(20,0)"); range.Property(r => r.ByteEnd).HasColumnType("numeric(20,0)");
        range.Property(r => r.ReceiptOrder).UseIdentityAlwaysColumn();
        range.HasIndex(r => new { r.BuildId, r.StepIndex, r.Stream, r.Start }).IsUnique();
        range.HasIndex(r => new { r.BuildId, r.Position }).IsUnique().HasFilter("\"Position\" IS NOT NULL");
        range.ToTable("RuntimeOutputRanges", t =>
        {
            t.HasCheckConstraint("CK_RuntimeOutputRanges_Kind", "\"Kind\" IN ('HEAD','TAIL','GAP')");
            t.HasCheckConstraint("CK_RuntimeOutputRanges_Sequence", "\"Start\" BETWEEN 0 AND 18446744073709551615 AND \"ByteStart\" BETWEEN 0 AND 18446744073709551615 AND ((\"End\" IS NOT NULL AND \"ByteEnd\" IS NOT NULL AND \"End\" > \"Start\" AND \"End\" <= 18446744073709551615 AND \"ByteEnd\" > \"ByteStart\" AND \"ByteEnd\" <= 18446744073709551615) OR (\"End\" IS NULL AND \"ByteEnd\" IS NULL AND \"Kind\" = 'GAP' AND \"Reason\" = 'producer-lost'))");
            t.HasCheckConstraint("CK_RuntimeOutputRanges_Content", "(\"Kind\" IN ('HEAD','TAIL') AND \"End\" IS NOT NULL AND \"ByteEnd\" IS NOT NULL AND \"End\" = \"Start\" + 1 AND \"ByteEnd\" - \"ByteStart\" BETWEEN 1 AND 65536 AND \"Sha256\" IS NOT NULL AND \"Sha256\" ~ '^[a-f0-9]{64}$' AND \"ObjectKey\" IS NOT NULL AND \"Reason\" IS NULL) OR (\"Kind\" = 'GAP' AND \"Sha256\" IS NULL AND \"ObjectKey\" IS NULL AND \"Reason\" IS NOT NULL AND \"Reason\" IN ('delivery-deadline','quota-exceeded','spool-overflow','storage-failure','producer-lost','retention'))");
        });
    }
}
