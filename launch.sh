#!/bin/bash
pkill -9 -f InfraDroneDesktop 2>/dev/null
pkill -9 -f "dotnet run" 2>/dev/null
sleep 1
export PATH=$HOME/.dotnet:$PATH
cd ~/infradrone-desktop
dotnet run
