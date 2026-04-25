#!/bin/bash
codedir=~/src/jimsfork/meridian59-dotnet
cd $codedir
./deploy.sh 
cd $codedir/bin
read
./Meridian59.TuiClient

