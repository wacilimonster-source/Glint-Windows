/* 在目标程序的界面线程上读取微信输入法当前高亮候选。
   只调用公开的 TSF 候选接口，不读取输入框正文，不记录按键。 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <oleauto.h>
#include <wchar.h>

#define WETYPE_TEXT_CAPACITY 120
#ifdef _WIN64
#define WETYPE_MAP_NAME L"Local\\Glint.WeTypeCandidate.v1"
#define WETYPE_POLL_MS 10
#define WETYPE_IDLE_TICKS 48
#define WETYPE_QUIET_TICKS 80
#else
#define WETYPE_MAP_NAME L"Local\\Glint.WeTypeCandidate.x86.v1"
#define WETYPE_MESSAGE_TAG 0x474C3332u
#pragma comment(linker, "/EXPORT:GlintWeTypeCallWndProc=_GlintWeTypeCallWndProc@12")
#endif

typedef struct Snapshot {
    volatile LONG sequence;
    DWORD processId;
    DWORD threadId;
    DWORD tick;
    DWORD charCount;
    volatile DWORD allowTick;
    wchar_t text[WETYPE_TEXT_CAPACITY];
} Snapshot;
typedef char SnapshotLayout[(sizeof(Snapshot) == 264) ? 1 : -1];

typedef struct WeTypeUnknown WeTypeUnknown;
typedef struct WeTypeUnknownVtbl {
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(WeTypeUnknown *self, REFIID iid, void **object);
    ULONG (STDMETHODCALLTYPE *AddRef)(WeTypeUnknown *self);
    ULONG (STDMETHODCALLTYPE *Release)(WeTypeUnknown *self);
} WeTypeUnknownVtbl;
struct WeTypeUnknown { WeTypeUnknownVtbl *lpVtbl; };

typedef struct WeTypeElement {
    struct WeTypeElementVtbl *lpVtbl;
} WeTypeElement;
typedef struct WeTypeElementVtbl {
    WeTypeUnknownVtbl unknown;
    HRESULT (STDMETHODCALLTYPE *GetDescription)(WeTypeElement *self, BSTR *description);
    HRESULT (STDMETHODCALLTYPE *GetGUID)(WeTypeElement *self, GUID *guid);
    HRESULT (STDMETHODCALLTYPE *Show)(WeTypeElement *self, BOOL show);
    HRESULT (STDMETHODCALLTYPE *IsShown)(WeTypeElement *self, BOOL *shown);
} WeTypeElementVtbl;

typedef struct WeTypeCandidates {
    struct WeTypeCandidatesVtbl *lpVtbl;
} WeTypeCandidates;
typedef struct WeTypeCandidatesVtbl {
    WeTypeElementVtbl element;
    HRESULT (STDMETHODCALLTYPE *GetUpdatedFlags)(WeTypeCandidates *self, DWORD *flags);
    HRESULT (STDMETHODCALLTYPE *GetDocumentMgr)(WeTypeCandidates *self, void **document);
    HRESULT (STDMETHODCALLTYPE *GetCount)(WeTypeCandidates *self, UINT *count);
    HRESULT (STDMETHODCALLTYPE *GetSelection)(WeTypeCandidates *self, UINT *index);
    HRESULT (STDMETHODCALLTYPE *GetString)(WeTypeCandidates *self, UINT index, BSTR *text);
} WeTypeCandidatesVtbl;

typedef struct WeTypeElementMgr {
    struct WeTypeElementMgrVtbl *lpVtbl;
} WeTypeElementMgr;
typedef struct WeTypeElementMgrVtbl {
    WeTypeUnknownVtbl unknown;
    HRESULT (STDMETHODCALLTYPE *BeginUIElement)(WeTypeElementMgr *self, void *element, BOOL *show, DWORD *id);
    HRESULT (STDMETHODCALLTYPE *UpdateUIElement)(WeTypeElementMgr *self, DWORD id);
    HRESULT (STDMETHODCALLTYPE *EndUIElement)(WeTypeElementMgr *self, DWORD id);
    HRESULT (STDMETHODCALLTYPE *GetUIElement)(WeTypeElementMgr *self, DWORD id, void **element);
    HRESULT (STDMETHODCALLTYPE *EnumUIElements)(WeTypeElementMgr *self, void **enumerator);
} WeTypeElementMgrVtbl;

typedef struct WeTypeEnum {
    struct WeTypeEnumVtbl *lpVtbl;
} WeTypeEnum;
typedef struct WeTypeEnumVtbl {
    WeTypeUnknownVtbl unknown;
    HRESULT (STDMETHODCALLTYPE *Clone)(WeTypeEnum *self, void **clone);
    HRESULT (STDMETHODCALLTYPE *Next)(WeTypeEnum *self, ULONG count, WeTypeElement **element, ULONG *fetched);
    HRESULT (STDMETHODCALLTYPE *Reset)(WeTypeEnum *self);
    HRESULT (STDMETHODCALLTYPE *Skip)(WeTypeEnum *self, ULONG count);
} WeTypeEnumVtbl;

#ifdef _WIN64
typedef struct ThreadState {
    UINT_PTR timer;
    int inside;
    int idle;
    int quiet;
} ThreadState;

static DWORD tlsIndex = TLS_OUT_OF_INDEXES;
static void TimerBody(ThreadState *state);
#endif
static const GUID IID_ITfUIElementMgr = {0xea1ea135, 0x19df, 0x11d7, {0xa6, 0xd2, 0x00, 0x06, 0x5b, 0x84, 0x43, 0x5c}};
static const GUID IID_ITfCandidateList = {0xea1ea138, 0x19df, 0x11d7, {0xa6, 0xd2, 0x00, 0x06, 0x5b, 0x84, 0x43, 0x5c}};

static BOOL Allowed(const Snapshot *snapshot) {
    DWORD now = GetTickCount();
    DWORD allow = snapshot->allowTick;
    DWORD foregroundProcess = 0;
    HWND focus;
    wchar_t className[64];
    if (allow == 0 || now - allow > 500) return FALSE;
    GetWindowThreadProcessId(GetForegroundWindow(), &foregroundProcess);
    if (foregroundProcess != GetCurrentProcessId()) return FALSE;
    focus = GetFocus();
    if (focus && (GetWindowLongPtrW(focus, GWL_STYLE) & ES_PASSWORD)) return FALSE;
    if (focus && GetClassNameW(focus, className, 64) > 0 &&
        (lstrcmpiW(className, L"ConsoleWindowClass") == 0 || lstrcmpiW(className, L"CASCADIA_HOSTING_WINDOW_CLASS") == 0))
        return FALSE;
    return TRUE;
}

static Snapshot *OpenSnapshot(HANDLE *handle) {
    void *view;
    *handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, WETYPE_MAP_NAME);
    if (!*handle) return NULL;
    view = MapViewOfFile(*handle, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Snapshot));
    if (!view) { CloseHandle(*handle); *handle = NULL; }
    return view;
}

static void CloseSnapshot(HANDLE handle, Snapshot *snapshot) {
    if (snapshot) UnmapViewOfFile(snapshot);
    if (handle) CloseHandle(handle);
}

static void Publish(Snapshot *snapshot, const wchar_t *text, DWORD permit) {
    LONG sequence = snapshot->sequence;
    size_t count = 0;
    if (permit && snapshot->allowTick != permit) return;
    if ((sequence & 1) || InterlockedCompareExchange(&snapshot->sequence, sequence | 1, sequence) != sequence) return;
    if (text) count = wcsnlen(text, WETYPE_TEXT_CAPACITY - 1);
    MemoryBarrier();
    snapshot->processId = GetCurrentProcessId();
    snapshot->threadId = GetCurrentThreadId();
    snapshot->tick = GetTickCount();
    snapshot->charCount = (DWORD)count;
    if (count) memcpy(snapshot->text, text, count * sizeof(wchar_t));
    snapshot->text[count] = L'\0';
    MemoryBarrier();
    if (permit && snapshot->allowTick != permit) {
        snapshot->charCount = 0;
        SecureZeroMemory(snapshot->text, sizeof(snapshot->text));
    }
    InterlockedIncrement(&snapshot->sequence);
}

#ifdef _WIN64
static void WriteText(const wchar_t *text) {
    HANDLE handle = NULL;
    Snapshot *snapshot = OpenSnapshot(&handle);
    if (!snapshot) return;
    Publish(snapshot, text, 0);
    CloseSnapshot(handle, snapshot);
}
#endif

static BOOL ReadSelected(wchar_t *text, size_t capacity) {
    HMODULE msctf;
    HRESULT (WINAPI *getThreadMgr)(WeTypeUnknown **manager);
    WeTypeUnknown *manager = NULL;
    WeTypeElementMgr *elements = NULL;
    WeTypeEnum *enumerator = NULL;
    WeTypeCandidates *selected = NULL;
    HRESULT result;
    BOOL found = FALSE;
    text[0] = L'\0';
    msctf = GetModuleHandleW(L"msctf.dll");
    if (!msctf) msctf = LoadLibraryW(L"msctf.dll");
    if (!msctf) return FALSE;
    getThreadMgr = (HRESULT (WINAPI *)(WeTypeUnknown **))GetProcAddress(msctf, "TF_GetThreadMgr");
    if (!getThreadMgr || FAILED(getThreadMgr(&manager)) || !manager) return FALSE;
    result = manager->lpVtbl->QueryInterface(manager, &IID_ITfUIElementMgr, (void **)&elements);
    manager->lpVtbl->Release(manager);
    if (FAILED(result) || !elements) return FALSE;
    result = elements->lpVtbl->EnumUIElements(elements, (void **)&enumerator);
    elements->lpVtbl->unknown.Release((WeTypeUnknown *)elements);
    if (FAILED(result) || !enumerator) return FALSE;
    for (;;) {
        WeTypeElement *element = NULL;
        WeTypeCandidates *candidate = NULL;
        ULONG fetched = 0;
        BOOL shown = FALSE;
        UINT count = 0, index = 0;
        BSTR value = NULL;
        result = enumerator->lpVtbl->Next(enumerator, 1, &element, &fetched);
        if (result != S_OK || fetched == 0 || !element) break;
        if (SUCCEEDED(element->lpVtbl->unknown.QueryInterface((WeTypeUnknown *)element, &IID_ITfCandidateList, (void **)&candidate)) && candidate) {
            element->lpVtbl->IsShown(element, &shown);
            if (SUCCEEDED(candidate->lpVtbl->GetCount(candidate, &count)) &&
                SUCCEEDED(candidate->lpVtbl->GetSelection(candidate, &index)) &&
                count > 0 && count <= 64 && index < count &&
                (shown || !selected) &&
                SUCCEEDED(candidate->lpVtbl->GetString(candidate, index, &value)) && value) {
                wcsncpy_s(text, capacity, value, _TRUNCATE);
                SysFreeString(value);
                found = text[0] != L'\0';
                if (selected) selected->lpVtbl->element.unknown.Release((WeTypeUnknown *)selected);
                selected = candidate;
                candidate = NULL;
                if (shown) { element->lpVtbl->unknown.Release((WeTypeUnknown *)element); break; }
            }
        }
        if (candidate) candidate->lpVtbl->element.unknown.Release((WeTypeUnknown *)candidate);
        element->lpVtbl->unknown.Release((WeTypeUnknown *)element);
    }
    if (selected) selected->lpVtbl->element.unknown.Release((WeTypeUnknown *)selected);
    enumerator->lpVtbl->unknown.Release((WeTypeUnknown *)enumerator);
    return found;
}

#ifdef _WIN64
static ThreadState *CurrentState(BOOL create) {
    ThreadState *state;
    if (tlsIndex == TLS_OUT_OF_INDEXES) return NULL;
    state = TlsGetValue(tlsIndex);
    if (state || !create) return state;
    state = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(ThreadState));
    if (state) TlsSetValue(tlsIndex, state);
    return state;
}

static void Stop(ThreadState *state) {
    if (!state || state->inside) return;
    if (state->timer) { KillTimer(NULL, state->timer); state->timer = 0; }
}

static void TimerBody(ThreadState *state) {
    HANDLE handle = NULL;
    Snapshot *snapshot;
    wchar_t text[WETYPE_TEXT_CAPACITY];
    if (!state) return;
    snapshot = OpenSnapshot(&handle);
    if (!snapshot) { state->quiet++; if (state->quiet > WETYPE_QUIET_TICKS) Stop(state); return; }
    state->quiet = 0;
    state->inside++;
    if (!Allowed(snapshot)) {
        CloseSnapshot(handle, snapshot);
        WriteText(NULL);
        state->idle++;
    } else if (ReadSelected(text, WETYPE_TEXT_CAPACITY)) {
        CloseSnapshot(handle, snapshot);
        WriteText(text);
        state->idle = 0;
    } else {
        CloseSnapshot(handle, snapshot);
        WriteText(NULL);
        state->idle++;
    }
    state->inside--;
    if (state->idle > WETYPE_IDLE_TICKS) Stop(state);
}

static void CALLBACK TimerProc(HWND hwnd, UINT message, UINT_PTR id, DWORD time) {
    (void)hwnd; (void)message; (void)id; (void)time;
    TimerBody(CurrentState(FALSE));
}

static void Ensure(void) {
    ThreadState *state = CurrentState(TRUE);
    if (!state || state->inside) return;
    if (!state->timer) state->timer = SetTimer(NULL, 0, WETYPE_POLL_MS, TimerProc);
    TimerBody(state);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)instance; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) tlsIndex = TlsAlloc();
    else if (reason == DLL_PROCESS_DETACH && tlsIndex != TLS_OUT_OF_INDEXES) TlsFree(tlsIndex);
    return TRUE;
}
#else
static void ReadFocused(HWND hwnd, DWORD permit) {
    HANDLE handle = NULL;
    Snapshot *snapshot = OpenSnapshot(&handle);
    wchar_t text[WETYPE_TEXT_CAPACITY];
    if (!snapshot) return;
    if (permit && snapshot->allowTick == permit && hwnd == GetFocus() && Allowed(snapshot)) {
        BOOL found = ReadSelected(text, WETYPE_TEXT_CAPACITY);
        if (snapshot->allowTick == permit && hwnd == GetFocus() && Allowed(snapshot))
            Publish(snapshot, found ? text : NULL, permit);
        SecureZeroMemory(text, sizeof(text));
    }
    CloseSnapshot(handle, snapshot);
}
#endif

#ifdef _WIN64
__declspec(dllexport)
#endif
LRESULT CALLBACK GlintWeTypeCallWndProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code == HC_ACTION) {
        const CWPSTRUCT *message = (const CWPSTRUCT *)lParam;
        if (message && message->message == WM_NULL) {
#ifdef _WIN64
            Ensure();
#else
            // A single synchronous sample keeps every native callback within the hook lifetime.
            if (message->wParam == WETYPE_MESSAGE_TAG) ReadFocused(message->hwnd, (DWORD)message->lParam);
#endif
        }
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}
