using Cake.Common;
using Cake.Common.Build;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Solution;
using Cake.Common.Tools.DotNetCore;
using Cake.Common.Tools.DotNetCore.Build;
using Cake.Common.Tools.DotNetCore.Pack;
using Cake.Common.Tools.DotNetCore.Restore;
using Cake.Common.Tools.DotNetCore.Test;
using Cake.Common.Tools.NuGet;
using Cake.Common.Tools.NuGet.Push;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.DocFx;
using Cake.DocFx.Build;
using Cake.DocFx.Metadata;
using CK.Text;
using SimpleGitVersion;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CodeCake
{
    /// <summary>
    /// Standard build "script".
    /// </summary>
    [AddPath( "CodeCakeBuilder/Tools" )]
    [AddPath( "CodeCakeBuilder/Tools/docfx" )]
    [AddPath( "packages/**/tools*" )]
    public class Build : CodeCakeHost
    {
        public Build()
        {
            Cake.Log.Verbosity = Verbosity.Diagnostic;

            const string solutionName = "DocFxCodeCake";
            const string solutionFileName = solutionName + ".sln";

            var releasesDir = Cake.Directory( "CodeCakeBuilder/Releases" );
            var ghPagesDir = Cake.Directory( "gh-pages" );
            var docfxConfigFilePath = Cake.File( "Documentation/docfx.json" );
            var docfxOutput = Cake.Directory( "Documentation/_site" );
            var docfxDir = Cake.Directory( "Documentation" );

            var projects = Cake.ParseSolution( solutionFileName )
                           .Projects
                           .Where( p => !(p is SolutionFolder)
                                        && p.Name != "CodeCakeBuilder" );

            // We do not publish .Tests projects for this solution.
            var projectsToPublish = projects
                                        .Where( p => !p.Path.Segments.Contains( "Tests" ) );

            SimpleRepositoryInfo gitInfo = Cake.GetSimpleRepositoryInfo();

            // Configuration is either "Debug" or "Release".
            string configuration = "Debug";

            Task( "Check-Repository" )
                .Does( () =>
                {
                    if( !gitInfo.IsValid )
                    {
                        if( Cake.IsInteractiveMode()
                            && Cake.ReadInteractiveOption( "Repository is not ready to be published. Proceed anyway?", 'Y', 'N' ) == 'Y' )
                        {
                            Cake.Warning( "GitInfo is not valid, but you choose to continue..." );
                        }
                        else if( !Cake.AppVeyor().IsRunningOnAppVeyor ) throw new Exception( "Repository is not ready to be published." );
                    }

                    if( gitInfo.IsValidRelease
                         && (gitInfo.PreReleaseName.Length == 0 || gitInfo.PreReleaseName == "rc") )
                    {
                        configuration = "Release";
                    }

                    Cake.Information( "Publishing {0} projects with version={1} and configuration={2}: {3}",
                        projectsToPublish.Count(),
                        gitInfo.SafeSemVersion,
                        configuration,
                        projectsToPublish.Select( p => p.Name ).Concatenate() );
                } );

            Task( "Unit-Testing" )
                .IsDependentOn( "Check-Repository" )
                .Does( () =>
                 {
                     Cake.DotNetCoreRestore();
                     var testDirectories = Cake.ParseSolution( solutionFileName )
                                                             .Projects
                                                                 .Where( p => p.Name.EndsWith( ".Tests" ) )
                                                                 .Select( p => p.Path.FullPath );
                     foreach( var test in testDirectories )
                     {
                         Cake.DotNetCoreTest( test );
                     }
                 } );

            Task( "Clean" )
                .IsDependentOn( "Check-Repository" )
                .IsDependentOn( "Unit-Testing" )
                .Does( () =>
                 {
                     Cake.CleanDirectories( projects.Select( p => p.Path.GetDirectory().Combine( "bin" ) ) );
                     Cake.CleanDirectories( releasesDir );
                 } );

            Task( "Restore-NuGet-Packages-With-Version" )
                .WithCriteria( () => gitInfo.IsValid )
               .IsDependentOn( "Clean" )
               .Does( () =>
                 {
                     // https://docs.microsoft.com/en-us/nuget/schema/msbuild-targets
                     Cake.DotNetCoreRestore( new DotNetCoreRestoreSettings().AddVersionArguments( gitInfo ) );
                 } );

            Task( "Build-With-Version" )
                .WithCriteria( () => gitInfo.IsValid )
                .IsDependentOn( "Check-Repository" )
                .IsDependentOn( "Unit-Testing" )
                .IsDependentOn( "Clean" )
                .IsDependentOn( "Restore-NuGet-Packages-With-Version" )
                .Does( () =>
                 {
                     foreach( var p in projectsToPublish )
                     {
                         Cake.DotNetCoreBuild( p.Path.GetDirectory().FullPath,
                             new DotNetCoreBuildSettings().AddVersionArguments( gitInfo, s =>
                             {
                                 s.Configuration = configuration;
                             } ) );
                     }
                 } );

            Task( "Create-NuGet-Packages" )
                .WithCriteria( () => gitInfo.IsValid )
                .IsDependentOn( "Build-With-Version" )
                .Does( () =>
                {
                    Cake.CreateDirectory( releasesDir );
                    foreach( SolutionProject p in projectsToPublish )
                    {
                        Cake.Warning( p.Path.GetDirectory().FullPath );
                        var s = new DotNetCorePackSettings();
                        s.ArgumentCustomization = args => args.Append( "--include-symbols" );
                        s.NoBuild = true;
                        s.Configuration = configuration;
                        s.OutputDirectory = releasesDir;
                        s.AddVersionArguments( gitInfo );
                        Cake.DotNetCorePack( p.Path.GetDirectory().FullPath, s );
                    }
                } );

            Task( "Push-NuGet-Packages" )
                //.WithCriteria( () => gitInfo.IsValid )
                .WithCriteria( () => false )
                .IsDependentOn( "Create-NuGet-Packages" )
                .Does( () =>
                {
                    IEnumerable<FilePath> nugetPackages = Cake.GetFiles( releasesDir.Path + "/*.nupkg" );
                    if( Cake.IsInteractiveMode() )
                    {
                        var localFeed = Cake.FindDirectoryAbove( "LocalFeed" );
                        if( localFeed != null )
                        {
                            Cake.Information( "LocalFeed directory found: {0}", localFeed );
                            if( Cake.ReadInteractiveOption( "Do you want to publish to LocalFeed?", 'Y', 'N' ) == 'Y' )
                            {
                                Cake.CopyFiles( nugetPackages, localFeed );
                            }
                        }
                    }
                    if( gitInfo.IsValidRelease )
                    {
                        if( gitInfo.PreReleaseName == ""
                            || gitInfo.PreReleaseName == "prerelease"
                            || gitInfo.PreReleaseName == "rc" )
                        {
                            PushNuGetPackages( "NUGET_API_KEY", "https://www.nuget.org/api/v2/package", nugetPackages );
                        }
                        else
                        {
                            // An alpha, beta, delta, epsilon, gamma, kappa goes to invenietis-preview.
                            PushNuGetPackages( "MYGET_PREVIEW_API_KEY", "https://www.myget.org/F/invenietis-preview/api/v2/package", nugetPackages );
                        }
                    }
                    else
                    {
                        Debug.Assert( gitInfo.IsValidCIBuild );
                        PushNuGetPackages( "MYGET_CI_API_KEY", "https://www.myget.org/F/invenietis-ci/api/v2/package", nugetPackages );
                    }
                    if( Cake.AppVeyor().IsRunningOnAppVeyor )
                    {
                        Cake.AppVeyor().UpdateBuildVersion( gitInfo.SafeNuGetVersion );
                    }
                } );

            Task( "Execute-DocFX" )
                //.WithCriteria(() => gitInfo.IsValid && gitInfo.PreReleaseName == "")
                .IsDependentOn( "Push-NuGet-Packages" )
                .Does( () =>
                 {
                     Cake.DocFxMetadata( docfxConfigFilePath );
                     Cake.DocFxBuild( docfxConfigFilePath );
                 } );

            Task( "Push-GitHub-Pages" )
                .WithCriteria( () => gitInfo.IsValid && gitInfo.PreReleaseName == "" )
                .IsDependentOn( "Execute-DocFX" )
                .Does( () =>
                {
                    //Exec( "git", $"--version" );
                    // Pull origin/gh-pages into local gh-pages
                    Exec( "git", $"pull origin gh-pages:gh-pages" );
                    // Checkout gh-pages branch in ghPagesDir
                    Exec( "git", $"worktree add {ghPagesDir} gh-pages" );
                    // Overwrite site with DocFX output
                    Cake.CopyDirectory( docfxOutput, ghPagesDir );
                    // Commit everything in gh-pages
                    Exec( "git", "add .", ghPagesDir );
                    Exec( "git", "commit -m \"Update gh-pages\"", ghPagesDir );
                    // Push gh-pages to remote
                    Exec( "git", "push origin gh-pages", ghPagesDir );
                    // Cleanup
                    Cake.DeleteDirectory( ghPagesDir, true );
                    Exec( "git", "worktree prune" );
                } );


            // The Default task for this script can be set here.
            Task( "Default" )
                .IsDependentOn( "Push-GitHub-Pages" );

        }

        void PushNuGetPackages( string apiKeyName, string pushUrl, IEnumerable<FilePath> nugetPackages )
        {
            // Resolves the API key.
            var apiKey = Cake.InteractiveEnvironmentVariable( apiKeyName );
            if( string.IsNullOrEmpty( apiKey ) )
            {
                Cake.Information( "Could not resolve {0}. Push to {1} is skipped.", apiKeyName, pushUrl );
            }
            else
            {
                var settings = new NuGetPushSettings
                {
                    Source = pushUrl,
                    ApiKey = apiKey,
                    Verbosity = NuGetVerbosity.Detailed
                };

                foreach( var nupkg in nugetPackages.Where( p => !p.FullPath.EndsWith( ".symbols.nupkg" ) ) )
                {
                    Cake.Information( $"Pushing '{nupkg}' to '{pushUrl}'." );
                    Cake.NuGetPush( nupkg, settings );
                }
            }
        }

        void Exec( string cmd, string args, DirectoryPath cwd = null )
        {
            string originalDir = Environment.CurrentDirectory;
            try
            {
                if( cwd != null )
                {
                    if( cwd.IsRelative )
                    {
                        cwd = cwd.MakeAbsolute( originalDir );
                    }
                    Environment.CurrentDirectory = cwd.FullPath;
                }
                using( var ps = Cake.StartAndReturnProcess( cmd, new ProcessSettings()
                {
                    Arguments = args,
                    //RedirectStandardError = true,
                    //RedirectStandardOutput = true,
                    WorkingDirectory = cwd
                } ) )
                {
                    ps.WaitForExit();
                }
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }
        }
    }
}
