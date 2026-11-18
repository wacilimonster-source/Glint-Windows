#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <tlhelp32.h>
#include <wchar.h>

#if !defined(_M_IX86)
#error WeTypeHook32 must be built for x86.
#endif

#define WETYPE_MESSAGE_TAG 0x474C3332u

typedef struct HookRequest {
    DWORD threadId;
    DWORD window;
    DWORD processId;
    DWORD permit;
} HookRequest;

static DWORD FindParent(void) {
    DWORD parent = 0;
    PROCESSENTRY32W entry = {0};
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (entry.th32ProcessID == GetCurrentProcessId()) {
                parent = entry.th32ParentProcessID;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return parent;
}

static BOOL OrdinarySameUser(HANDLE process) {
    HANDLE ownToken = NULL, targetToken = NULL;
    DWORD size, elevated, uiAccess;
    BYTE ownUser[256], targetUser[256], integrity[256];
    PSID sid;
    BOOL allowed = FALSE;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &ownToken) ||
        !OpenProcessToken(process, TOKEN_QUERY, &targetToken)) goto done;
    if (!GetTokenInformation(ownToken, TokenUser, ownUser, sizeof(ownUser), &size) ||
        !GetTokenInformation(targetToken, TokenUser, targetUser, sizeof(targetUser), &size) ||
        !EqualSid(((TOKEN_USER *)ownUser)->User.Sid, ((TOKEN_USER *)targetUser)->User.Sid)) goto done;
    if (!GetTokenInformation(targetToken, TokenElevation, &elevated, sizeof(elevated), &size) || elevated ||
        !GetTokenInformation(targetToken, TokenUIAccess, &uiAccess, sizeof(uiAccess), &size) || uiAccess ||
        !GetTokenInformation(targetToken, TokenIntegrityLevel, integrity, sizeof(integrity), &size)) goto done;
    sid = ((TOKEN_MANDATORY_LABEL *)integrity)->Label.Sid;
    if (IsValidSid(sid) && *GetSidSubAuthorityCount(sid) > 0)
        allowed = *GetSidSubAuthority(sid, *GetSidSubAuthorityCount(sid) - 1) <= SECURITY_MANDATORY_MEDIUM_RID;
done:
    if (targetToken) CloseHandle(targetToken);
    if (ownToken) CloseHandle(ownToken);
    return allowed;
}

static BOOL IsTargetProcess(DWORD pid, DWORD session, const wchar_t *expectedPath) {
    HANDLE process;
    DWORD targetSession, length = MAX_PATH;
    USHORT machine, native;
    wchar_t path[MAX_PATH];
    BOOL allowed;
    if (!ProcessIdToSessionId(pid, &targetSession) || targetSession != session) return FALSE;
    process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return FALSE;
    allowed = IsWow64Process2(process, &machine, &native) &&
        machine == IMAGE_FILE_MACHINE_I386 && native == IMAGE_FILE_MACHINE_AMD64 &&
        QueryFullProcessImageNameW(process, 0, path, &length) &&
        CompareStringOrdinal(path, -1, expectedPath, -1, TRUE) == CSTR_EQUAL &&
        OrdinarySameUser(process);
    CloseHandle(process);
    return allowed;
}

static BOOL IsFocusedWindow(HWND hwnd, const HookRequest *request) {
    DWORD pid = 0;
    GUITHREADINFO info = {0};
    HWND foreground = GetForegroundWindow();
    info.cbSize = sizeof(info);
    if (!hwnd || !request->threadId || !request->processId ||
        GetWindowThreadProcessId(hwnd, &pid) != request->threadId || pid != request->processId ||
        GetAncestor(hwnd, GA_ROOT) != foreground ||
        !GetGUIThreadInfo(request->threadId, &info) || info.hwndFocus != hwnd ||
        (GetWindowLongPtrW(hwnd, GWL_STYLE) & ES_PASSWORD)) return FALSE;
    GetWindowThreadProcessId(foreground, &pid);
    return pid == request->processId && GetForegroundWindow() == foreground;
}

static DWORD Poll(const HookRequest *request, DWORD session, const wchar_t *expectedPath, HMODULE module, HOOKPROC procedure) {
    DWORD result;
    HHOOK hook;
    DWORD_PTR ignored;
    HWND hwnd = (HWND)(UINT_PTR)request->window;
    if (!request->permit || !IsFocusedWindow(hwnd, request) ||
        !IsTargetProcess(request->processId, session, expectedPath) ||
        !IsFocusedWindow(hwnd, request)) return ERROR_ACCESS_DENIED;
    hook = SetWindowsHookExW(WH_CALLWNDPROC, procedure, module, request->threadId);
    if (!hook) return GetLastError();
    SetLastError(ERROR_TIMEOUT);
    result = SendMessageTimeoutW(hwnd, WM_NULL, WETYPE_MESSAGE_TAG, (LPARAM)request->permit,
                                 SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, 80, &ignored)
        ? ERROR_SUCCESS : GetLastError();
    if (!UnhookWindowsHookEx(hook)) ExitProcess(GetLastError());
    return result;
}

static BOOL Transfer(HANDLE pipe, void *buffer, DWORD count, BOOL write) {
    DWORD done;
    if (write) return WriteFile(pipe, buffer, count, &done, NULL) && done == count;
    while (count) {
        if (!ReadFile(pipe, buffer, count, &done, NULL) || !done) return FALSE;
        buffer = (BYTE *)buffer + done;
        count -= done;
    }
    return TRUE;
}

int wmain(int argc, wchar_t **argv) {
    wchar_t pipePath[96], dllPath[MAX_PATH], targetPath[MAX_PATH];
    wchar_t *file;
    HANDLE pipe;
    HMODULE module;
    union { FARPROC address; HOOKPROC hook; } procedure;
    DWORD parent, server, session, serverSession, size;
    DWORD hello[] = {1, IMAGE_FILE_MACHINE_I386};
    HookRequest request;
    if (argc != 2 || wcslen(argv[1]) != 32 || wcsspn(argv[1], L"0123456789abcdef") != 32 ||
        swprintf_s(pipePath, _countof(pipePath), L"\\\\.\\pipe\\Glint.WeTypeHook32.%ls", argv[1]) < 0)
        return 1;
    parent = FindParent();
    if (!parent || !ProcessIdToSessionId(GetCurrentProcessId(), &session)) return 2;
    pipe = CreateFileW(pipePath, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
                       SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
    if (pipe == INVALID_HANDLE_VALUE) return 3;
    if (!GetNamedPipeServerProcessId(pipe, &server) || server != parent ||
        !GetNamedPipeServerSessionId(pipe, &serverSession) || serverSession != session) return 4;
    size = GetModuleFileNameW(NULL, dllPath, _countof(dllPath));
    if (!size || size >= _countof(dllPath) || !(file = wcsrchr(dllPath, L'\\')) ||
        wcscpy_s(file + 1, _countof(dllPath) - (size_t)(file + 1 - dllPath), L"WeTypeSink32.dll") ||
        FAILED(SHGetFolderPathW(NULL, CSIDL_PROGRAM_FILESX86, NULL, SHGFP_TYPE_CURRENT, targetPath)) ||
        wcscat_s(targetPath, _countof(targetPath), L"\\WXWork\\WXWork.exe")) return 5;
    module = LoadLibraryExW(dllPath, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return 6;
    procedure.address = GetProcAddress(module, "GlintWeTypeCallWndProc");
    if (!procedure.address || !Transfer(pipe, hello, sizeof(hello), TRUE)) return 7;
    while (Transfer(pipe, &request, sizeof(request), FALSE)) {
        DWORD status = Poll(&request, session, targetPath, module, procedure.hook);
        if (!Transfer(pipe, &status, sizeof(status), TRUE)) break;
    }
    FreeLibrary(module);
    CloseHandle(pipe);
    return 0;
}
