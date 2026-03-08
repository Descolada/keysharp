fn := () => FileAppend("F", "*")

BlockWithoutPumping(ms) {
    if (A_OSType = "WIN32_NT")
        DllCall("kernel32.dll\Sleep", "uint", ms)
    else if (A_OSType = "UNIX")
        DllCall("libc.so.6\usleep", "uint", ms * 1000)
    else if (A_OSType = "MACOSX")
        DllCall("libc.dylib\usleep", "uint", ms * 1000)
    else
        Sleep(ms)
}

SetTimer(fn, 80)
BlockWithoutPumping(260)
Sleep(40)
SetTimer(fn, 0)
FileAppend("|done", "*")
