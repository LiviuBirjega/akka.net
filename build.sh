#!/usr/bin/env bash
##########################################################################
# This is the Fake bootstrapper script for Linux and OS X.
##########################################################################

# Define directories.
SCRIPT_DIR=$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )
TOOLS_DIR=$SCRIPT_DIR/tools
INCREMENTALIST_DIR=$TOOLS_DIR/incrementalist
INCREMENTALIST_EXE=$INCREMENTALIST_DIR/Incrementalist.Cmd.exe
NUGET_EXE=$TOOLS_DIR/nuget.exe
NUGET_URL=https://dist.nuget.org/win-x86-commandline/v5.8.0/nuget.exe
FAKE_VERSION=4.63.0
FAKE_EXE=$TOOLS_DIR/FAKE/tools/FAKE.exe
DOTNET_EXE=$SCRIPT_DIR/.dotnet/dotnet
DOTNETCORE_VERSION=3.1.105
DOTNET_VERSION=5.0.101
DOTNET_INSTALLER_URL=https://dot.net/v1/dotnet-install.sh
DOTNET_CHANNEL=LTS
PROTOBUF_VERSION=3.21.5
INCREMENTALIST_VERSION=0.9.0

# Define default arguments.
TARGET="Default"
CONFIGURATION="Release"
VERBOSITY="verbose"
DRYRUN=
SCRIPT_ARGUMENTS=()

# Parse arguments.
for i in "$@"; do
    case $1 in
        -t|--target) TARGET="$2"; shift ;;
        -c|--configuration) CONFIGURATION="$2"; shift ;;
        -v|--verbosity) VERBOSITY="$2"; shift ;;
        -d|--dryrun) DRYRUN="-dryrun" ;;
        --) shift; SCRIPT_ARGUMENTS+=("$@"); break ;;
        *) SCRIPT_ARGUMENTS+=("$1") ;;
    esac
    shift
done

# Make sure the tools folder exist.
if [ ! -d "$TOOLS_DIR" ]; then
  mkdir "$TOOLS_DIR"
fi

###########################################################################
# INSTALL NUGET
###########################################################################

# Download NuGet if it does not exist.
if [ ! -f "$NUGET_EXE" ]; then
    echo "Downloading NuGet..."
    curl -Lsfo "$NUGET_EXE" $NUGET_URL
    if [ $? -ne 0 ]; then
        echo "An error occured while downloading nuget.exe."
        exit 1
    fi
fi

###########################################################################
# INSTALL FAKE
###########################################################################

if [ ! -f "$FAKE_EXE" ]; then
    mono "$NUGET_EXE" install Fake -ExcludeVersion -Version $FAKE_VERSION -OutputDirectory "$TOOLS_DIR"
    if [ $? -ne 0 ]; then
        echo "An error occured while installing Cake."
        exit 1
    fi
fi

# Make sure that Fake has been installed.
if [ ! -f "$FAKE_EXE" ]; then
    echo "Could not find Fake.exe at '$FAKE_EXE'."
    exit 1
fi

###########################################################################
# INSTALL Incrementalist
###########################################################################
if [ ! -f "$INCREMENTALIST_EXE" ]; then
    dotnet tool install Incrementalist.Cmd --version $INCREMENTALIST_VERSION --tool-path "$INCREMENTALIST_DIR"
    if [ $? -ne 0 ]; then
        echo "Incrementalist already installed."
    fi
fi

###########################################################################
# WORKAROUND FOR MONO
###########################################################################
export FrameworkPathOverride=/usr/lib/mono/4.5/

###########################################################################
# RUN BUILD SCRIPT
###########################################################################

# Start Fake
exec mono "$FAKE_EXE" build.fsx "${SCRIPT_ARGUMENTS[@]}" --verbosity=$VERBOSITY --configuration=$CONFIGURATION --target=$TARGET $DRYRUN
