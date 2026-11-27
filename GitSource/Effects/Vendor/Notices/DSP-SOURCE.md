# NeonLoop audio processor source

`RomScoreDSP.dll` contains Rubber Band 4.0.0 and is distributed under the GNU General Public License, version 2 or later. Its complete corresponding source is in **DSP Source** beside this notice in the installed package. The same code is in `GitSource/Effects` in this release’s source. Copyright and upstream notices are retained. The installation terms place no additional restrictions on use and redistribution under that license.

To rebuild just this DLL, open an x64 Native Tools Command Prompt for Visual Studio, change to the `DSP Source` folder, and run:

```bat
cl /nologo /std:c++17 /EHsc /LD /MT /O2 /DNOMINMAX /IEffects\Vendor\rubberband-4.0.0 /IEffects\Vendor\minimp3 Effects\Music\ScoreDsp.cpp Effects\Music\Mp3Score.cpp Effects\Vendor\rubberband-4.0.0\single\RubberBandSingle.cpp /link /OUT:RomScoreDSP.dll
```

The copy of the GPL is at `DSP Source/Effects/Vendor/rubberband-4.0.0/COPYING`. NeonLoop’s original DSP wrapper retains its MIT grant in `LICENSE`. [Rubber Band licensing](https://breakfastquay.com/rubberband/license.html)
