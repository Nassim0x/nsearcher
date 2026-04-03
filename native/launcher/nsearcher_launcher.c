#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#define PIPE_CONNECT_TIMEOUT_MS 4
#define PIPE_RETRY_COUNT 32
#define PIPE_RETRY_DELAY_MS 2
#define SERVER_FILENAME L"NSearcher.Server.exe"
#define INTERNAL_DAEMON_ARGUMENT L"--internal-daemon"
#define DIRECT_OUTPUT_STDOUT 0x01
#define DIRECT_OUTPUT_STDERR 0x02

typedef struct
{
    unsigned char* data;
    size_t length;
    size_t capacity;
} ByteBuffer;

typedef struct
{
    wchar_t** items;
    int count;
} WideArgList;

typedef struct
{
    int exitCode;
    const unsigned char* stdoutData;
    size_t stdoutLength;
    const unsigned char* stderrData;
    size_t stderrLength;
} DaemonResponse;

static void BufferFree(ByteBuffer* buffer)
{
    free(buffer->data);
    buffer->data = NULL;
    buffer->length = 0;
    buffer->capacity = 0;
}

static bool BufferEnsure(ByteBuffer* buffer, size_t additionalLength)
{
    size_t required = buffer->length + additionalLength;
    if (required <= buffer->capacity)
    {
        return true;
    }

    size_t nextCapacity = buffer->capacity == 0 ? 256 : buffer->capacity;
    while (nextCapacity < required)
    {
        nextCapacity *= 2;
    }

    unsigned char* next = (unsigned char*)realloc(buffer->data, nextCapacity);
    if (next == NULL)
    {
        return false;
    }

    buffer->data = next;
    buffer->capacity = nextCapacity;
    return true;
}

static bool BufferAppend(ByteBuffer* buffer, const void* data, size_t dataLength)
{
    if (!BufferEnsure(buffer, dataLength))
    {
        return false;
    }

    memcpy(buffer->data + buffer->length, data, dataLength);
    buffer->length += dataLength;
    return true;
}

static bool BufferAppendByte(ByteBuffer* buffer, unsigned char value)
{
    return BufferAppend(buffer, &value, sizeof(value));
}

static bool BufferAppendInt32(ByteBuffer* buffer, int value)
{
    return BufferAppend(buffer, &value, sizeof(value));
}

static bool BufferAppendInt64(ByteBuffer* buffer, int64_t value)
{
    return BufferAppend(buffer, &value, sizeof(value));
}

static bool BufferAppend7BitEncodedUInt32(ByteBuffer* buffer, uint32_t value)
{
    while (value >= 0x80u)
    {
        if (!BufferAppendByte(buffer, (unsigned char)(value | 0x80u)))
        {
            return false;
        }

        value >>= 7;
    }

    return BufferAppendByte(buffer, (unsigned char)value);
}

static bool BufferAppendUtf8String(ByteBuffer* buffer, const wchar_t* text)
{
    int utf8Length = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (utf8Length <= 0)
    {
        return false;
    }

    uint32_t payloadLength = (uint32_t)(utf8Length - 1);
    if (!BufferAppend7BitEncodedUInt32(buffer, payloadLength))
    {
        return false;
    }

    if (payloadLength == 0)
    {
        return true;
    }

    if (!BufferEnsure(buffer, payloadLength))
    {
        return false;
    }

    int bytesWritten = WideCharToMultiByte(
        CP_UTF8,
        0,
        text,
        -1,
        (char*)(buffer->data + buffer->length),
        utf8Length,
        NULL,
        NULL);

    if (bytesWritten != utf8Length)
    {
        return false;
    }

    buffer->length += payloadLength;
    return true;
}

static bool ReadExactly(HANDLE handle, void* buffer, DWORD length)
{
    unsigned char* cursor = (unsigned char*)buffer;
    DWORD remaining = length;

    while (remaining > 0)
    {
        DWORD bytesRead = 0;
        if (!ReadFile(handle, cursor, remaining, &bytesRead, NULL) || bytesRead == 0)
        {
            return false;
        }

        cursor += bytesRead;
        remaining -= bytesRead;
    }

    return true;
}

static bool WriteExactly(HANDLE handle, const void* buffer, DWORD length)
{
    const unsigned char* cursor = (const unsigned char*)buffer;
    DWORD remaining = length;

    while (remaining > 0)
    {
        DWORD bytesWritten = 0;
        if (!WriteFile(handle, cursor, remaining, &bytesWritten, NULL))
        {
            return false;
        }

        cursor += bytesWritten;
        remaining -= bytesWritten;
    }

    return true;
}

static bool Read7BitEncodedUInt32(const unsigned char* payload, size_t payloadLength, size_t* offset, uint32_t* value)
{
    uint32_t result = 0;
    int shift = 0;

    while (*offset < payloadLength && shift < 35)
    {
        unsigned char current = payload[(*offset)++];
        result |= ((uint32_t)(current & 0x7Fu)) << shift;

        if ((current & 0x80u) == 0)
        {
            *value = result;
            return true;
        }

        shift += 7;
    }

    return false;
}

static bool ReadInt32Value(const unsigned char* payload, size_t payloadLength, size_t* offset, int* value)
{
    if (*offset + sizeof(int) > payloadLength)
    {
        return false;
    }

    memcpy(value, payload + *offset, sizeof(int));
    *offset += sizeof(int);
    return true;
}

static bool ReadUtf8StringView(
    const unsigned char* payload,
    size_t payloadLength,
    size_t* offset,
    const unsigned char** stringData,
    size_t* stringLength)
{
    uint32_t byteLength = 0;
    if (!Read7BitEncodedUInt32(payload, payloadLength, offset, &byteLength))
    {
        return false;
    }

    if (*offset + byteLength > payloadLength)
    {
        return false;
    }

    *stringData = payload + *offset;
    *stringLength = byteLength;
    *offset += byteLength;
    return true;
}

static bool IsConsoleHandle(HANDLE handle)
{
    DWORD mode = 0;
    return GetConsoleMode(handle, &mode) != 0;
}

static void WriteUtf8BufferToHandle(HANDLE handle, const unsigned char* data, size_t dataLength)
{
    if (dataLength == 0)
    {
        return;
    }

    if (!IsConsoleHandle(handle))
    {
        WriteExactly(handle, data, (DWORD)dataLength);
        return;
    }

    int wideLength = MultiByteToWideChar(CP_UTF8, 0, (const char*)data, (int)dataLength, NULL, 0);
    if (wideLength <= 0)
    {
        WriteExactly(handle, data, (DWORD)dataLength);
        return;
    }

    wchar_t* wideBuffer = (wchar_t*)malloc((size_t)(wideLength + 1) * sizeof(wchar_t));
    if (wideBuffer == NULL)
    {
        WriteExactly(handle, data, (DWORD)dataLength);
        return;
    }

    MultiByteToWideChar(CP_UTF8, 0, (const char*)data, (int)dataLength, wideBuffer, wideLength);
    wideBuffer[wideLength] = L'\0';

    DWORD written = 0;
    if (!WriteConsoleW(handle, wideBuffer, (DWORD)wideLength, &written, NULL))
    {
        WriteExactly(handle, data, (DWORD)dataLength);
    }

    free(wideBuffer);
}

static bool GetCurrentDirectoryUtf16(wchar_t** directory)
{
    DWORD required = GetCurrentDirectoryW(0, NULL);
    if (required == 0)
    {
        return false;
    }

    wchar_t* buffer = (wchar_t*)malloc((size_t)required * sizeof(wchar_t));
    if (buffer == NULL)
    {
        return false;
    }

    if (GetCurrentDirectoryW(required, buffer) == 0)
    {
        free(buffer);
        return false;
    }

    *directory = buffer;
    return true;
}

static wchar_t* DuplicateWideString(const wchar_t* text)
{
    size_t length = wcslen(text);
    wchar_t* duplicate = (wchar_t*)malloc((length + 1) * sizeof(wchar_t));
    if (duplicate == NULL)
    {
        return NULL;
    }

    memcpy(duplicate, text, (length + 1) * sizeof(wchar_t));
    return duplicate;
}

static bool BuildPipeName(wchar_t** pipeName)
{
    wchar_t userName[256];
    DWORD userNameLength = (DWORD)(sizeof(userName) / sizeof(userName[0]));
    if (!GetEnvironmentVariableW(L"USERNAME", userName, userNameLength))
    {
        userName[0] = L'\0';
    }

    wchar_t sanitized[320];
    size_t offset = 0;
    const wchar_t* prefix = L"nsearcher-";
    size_t prefixLength = wcslen(prefix);
    memcpy(sanitized, prefix, prefixLength * sizeof(wchar_t));
    offset += prefixLength;

    for (const wchar_t* cursor = userName; *cursor != L'\0'; cursor++)
    {
        if (iswalnum(*cursor))
        {
            sanitized[offset++] = towlower(*cursor);
        }
    }

    if (offset == prefixLength)
    {
        const wchar_t* fallback = L"user";
        size_t fallbackLength = wcslen(fallback);
        memcpy(sanitized + offset, fallback, fallbackLength * sizeof(wchar_t));
        offset += fallbackLength;
    }

    sanitized[offset] = L'\0';
    *pipeName = DuplicateWideString(sanitized);
    return *pipeName != NULL;
}

static bool BuildServerPath(wchar_t** serverPath)
{
    wchar_t modulePath[MAX_PATH];
    DWORD length = GetModuleFileNameW(NULL, modulePath, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
    {
        return false;
    }

    wchar_t* separator = wcsrchr(modulePath, L'\\');
    if (separator == NULL)
    {
        return false;
    }

    separator[1] = L'\0';
    size_t baseLength = wcslen(modulePath);
    size_t fileNameLength = wcslen(SERVER_FILENAME);

    wchar_t* buffer = (wchar_t*)malloc((baseLength + fileNameLength + 1) * sizeof(wchar_t));
    if (buffer == NULL)
    {
        return false;
    }

    memcpy(buffer, modulePath, baseLength * sizeof(wchar_t));
    memcpy(buffer + baseLength, SERVER_FILENAME, (fileNameLength + 1) * sizeof(wchar_t));
    *serverPath = buffer;
    return true;
}

static bool AppendQuotedArgumentWide(ByteBuffer* buffer, const wchar_t* argument)
{
    if (!BufferAppend(buffer, L"\"", sizeof(wchar_t)))
    {
        return false;
    }

    for (const wchar_t* cursor = argument; *cursor != L'\0'; cursor++)
    {
        if (*cursor == L'"')
        {
            if (!BufferAppend(buffer, L"\\", sizeof(wchar_t)))
            {
                return false;
            }
        }

        if (!BufferAppend(buffer, cursor, sizeof(wchar_t)))
        {
            return false;
        }
    }

    return BufferAppend(buffer, L"\"", sizeof(wchar_t));
}

static bool BuildProcessCommandLine(
    const wchar_t* executablePath,
    int argumentCount,
    wchar_t** arguments,
    wchar_t** commandLine)
{
    ByteBuffer buffer = { 0 };

    if (!AppendQuotedArgumentWide(&buffer, executablePath))
    {
        BufferFree(&buffer);
        return false;
    }

    for (int index = 0; index < argumentCount; index++)
    {
        if (!BufferAppend(&buffer, L" ", sizeof(wchar_t)) ||
            !AppendQuotedArgumentWide(&buffer, arguments[index]))
        {
            BufferFree(&buffer);
            return false;
        }
    }

    if (!BufferAppend(&buffer, L"\0", sizeof(wchar_t)))
    {
        BufferFree(&buffer);
        return false;
    }

    *commandLine = (wchar_t*)buffer.data;
    return true;
}

static int RunServerDirect(int argumentCount, wchar_t** arguments)
{
    wchar_t* serverPath = NULL;
    wchar_t* commandLine = NULL;
    int exitCode = 2;

    if (!BuildServerPath(&serverPath) ||
        !BuildProcessCommandLine(serverPath, argumentCount, arguments, &commandLine))
    {
        goto cleanup;
    }

    STARTUPINFOW startupInfo;
    PROCESS_INFORMATION processInformation;
    ZeroMemory(&startupInfo, sizeof(startupInfo));
    ZeroMemory(&processInformation, sizeof(processInformation));

    startupInfo.cb = sizeof(startupInfo);
    startupInfo.dwFlags = STARTF_USESTDHANDLES;
    startupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    startupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);

    if (!CreateProcessW(
            serverPath,
            commandLine,
            NULL,
            NULL,
            TRUE,
            0,
            NULL,
            NULL,
            &startupInfo,
            &processInformation))
    {
        goto cleanup;
    }

    WaitForSingleObject(processInformation.hProcess, INFINITE);
    if (!GetExitCodeProcess(processInformation.hProcess, (DWORD*)&exitCode))
    {
        exitCode = 2;
    }

    CloseHandle(processInformation.hThread);
    CloseHandle(processInformation.hProcess);

cleanup:
    free(serverPath);
    free(commandLine);
    return exitCode;
}

static bool StartServerDaemon(const wchar_t* pipeName)
{
    wchar_t* serverPath = NULL;
    wchar_t* daemonArgs[2];
    wchar_t* commandLine = NULL;
    bool started = false;

    if (!BuildServerPath(&serverPath))
    {
        return false;
    }

    daemonArgs[0] = (wchar_t*)INTERNAL_DAEMON_ARGUMENT;
    daemonArgs[1] = (wchar_t*)pipeName;

    if (!BuildProcessCommandLine(serverPath, 2, daemonArgs, &commandLine))
    {
        goto cleanup;
    }

    STARTUPINFOW startupInfo;
    PROCESS_INFORMATION processInformation;
    ZeroMemory(&startupInfo, sizeof(startupInfo));
    ZeroMemory(&processInformation, sizeof(processInformation));
    startupInfo.cb = sizeof(startupInfo);

    if (!CreateProcessW(
            serverPath,
            commandLine,
            NULL,
            NULL,
            FALSE,
            CREATE_NO_WINDOW,
            NULL,
            NULL,
            &startupInfo,
            &processInformation))
    {
        goto cleanup;
    }

    CloseHandle(processInformation.hThread);
    CloseHandle(processInformation.hProcess);
    started = true;

cleanup:
    free(serverPath);
    free(commandLine);
    return started;
}

static HANDLE ConnectToPipe(const wchar_t* pipeName)
{
    wchar_t fullPipeName[384];
    _snwprintf_s(fullPipeName, sizeof(fullPipeName) / sizeof(fullPipeName[0]), _TRUNCATE, L"\\\\.\\pipe\\%ls", pipeName);

    for (int attempt = 0; attempt < PIPE_RETRY_COUNT; attempt++)
    {
        HANDLE pipe = CreateFileW(
            fullPipeName,
            GENERIC_READ | GENERIC_WRITE,
            0,
            NULL,
            OPEN_EXISTING,
            0,
            NULL);

        if (pipe != INVALID_HANDLE_VALUE)
        {
            return pipe;
        }

        if (GetLastError() == ERROR_PIPE_BUSY)
        {
            WaitNamedPipeW(fullPipeName, PIPE_CONNECT_TIMEOUT_MS);
        }

        Sleep(PIPE_RETRY_DELAY_MS);
    }

    return INVALID_HANDLE_VALUE;
}

static bool BuildRequestPayload(
    const wchar_t* workingDirectory,
    bool disableColor,
    unsigned char directOutputFlags,
    int clientProcessId,
    HANDLE stdoutHandle,
    HANDLE stderrHandle,
    int argumentCount,
    wchar_t** arguments,
    ByteBuffer* payload)
{
    if (!BufferAppendUtf8String(payload, workingDirectory) ||
        !BufferAppendByte(payload, disableColor ? 1 : 0) ||
        !BufferAppendInt32(payload, argumentCount))
    {
        return false;
    }

    for (int index = 0; index < argumentCount; index++)
    {
        if (!BufferAppendUtf8String(payload, arguments[index]))
        {
            return false;
        }
    }

    return BufferAppendByte(payload, directOutputFlags) &&
        BufferAppendInt32(payload, clientProcessId) &&
        BufferAppendInt64(payload, (int64_t)(intptr_t)stdoutHandle) &&
        BufferAppendInt64(payload, (int64_t)(intptr_t)stderrHandle);
}

static bool SendRequest(HANDLE pipe, const ByteBuffer* payload)
{
    int frameLength = (int)payload->length;
    return WriteExactly(pipe, &frameLength, sizeof(frameLength)) &&
        WriteExactly(pipe, payload->data, frameLength);
}

static bool ReadResponse(HANDLE pipe, ByteBuffer* payload)
{
    int frameLength = 0;
    if (!ReadExactly(pipe, &frameLength, sizeof(frameLength)) || frameLength < 0)
    {
        return false;
    }

    if (!BufferEnsure(payload, (size_t)frameLength))
    {
        return false;
    }

    payload->length = (size_t)frameLength;
    return ReadExactly(pipe, payload->data, (DWORD)payload->length);
}

static bool ParseResponse(const ByteBuffer* payload, DaemonResponse* response)
{
    size_t offset = 0;

    if (!ReadInt32Value(payload->data, payload->length, &offset, &response->exitCode) ||
        !ReadUtf8StringView(payload->data, payload->length, &offset, &response->stdoutData, &response->stdoutLength) ||
        !ReadUtf8StringView(payload->data, payload->length, &offset, &response->stderrData, &response->stderrLength))
    {
        return false;
    }

    return true;
}

int wmain(void)
{
    int argumentCount = 0;
    wchar_t** arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    if (arguments == NULL)
    {
        return 2;
    }

    int forwardedArgumentCount = argumentCount > 0 ? argumentCount - 1 : 0;
    wchar_t** forwardedArguments = arguments + 1;

    bool needsDirectServer = forwardedArgumentCount == 0;
    for (int index = 0; index < forwardedArgumentCount && !needsDirectServer; index++)
    {
        if (_wcsicmp(forwardedArguments[index], L"--help") == 0 ||
            _wcsicmp(forwardedArguments[index], L"-h") == 0)
        {
            needsDirectServer = true;
        }
    }

    if (needsDirectServer)
    {
        int exitCode = RunServerDirect(forwardedArgumentCount, forwardedArguments);
        LocalFree(arguments);
        return exitCode;
    }

    wchar_t* workingDirectory = NULL;
    wchar_t* pipeName = NULL;
    ByteBuffer requestPayload = { 0 };
    ByteBuffer responsePayload = { 0 };
    HANDLE pipe = INVALID_HANDLE_VALUE;
    HANDLE stdoutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE stderrHandle = GetStdHandle(STD_ERROR_HANDLE);
    unsigned char directOutputFlags = 0;
    int exitCode = 2;

    if (!GetCurrentDirectoryUtf16(&workingDirectory) ||
        !BuildPipeName(&pipeName))
    {
        goto cleanup;
    }

    if (!IsConsoleHandle(stdoutHandle))
    {
        directOutputFlags |= DIRECT_OUTPUT_STDOUT;
    }

    if (!IsConsoleHandle(stderrHandle))
    {
        directOutputFlags |= DIRECT_OUTPUT_STDERR;
    }

    pipe = ConnectToPipe(pipeName);
    if (pipe == INVALID_HANDLE_VALUE)
    {
        if (!StartServerDaemon(pipeName))
        {
            goto cleanup;
        }

        pipe = ConnectToPipe(pipeName);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            goto cleanup;
        }
    }

    if (!BuildRequestPayload(
            workingDirectory,
            !IsConsoleHandle(stdoutHandle),
            directOutputFlags,
            (int)GetCurrentProcessId(),
            stdoutHandle,
            stderrHandle,
            forwardedArgumentCount,
            forwardedArguments,
            &requestPayload) ||
        !SendRequest(pipe, &requestPayload) ||
        !ReadResponse(pipe, &responsePayload))
    {
        goto cleanup;
    }

    DaemonResponse response;
    if (!ParseResponse(&responsePayload, &response))
    {
        goto cleanup;
    }

    WriteUtf8BufferToHandle(GetStdHandle(STD_OUTPUT_HANDLE), response.stdoutData, response.stdoutLength);
    WriteUtf8BufferToHandle(GetStdHandle(STD_ERROR_HANDLE), response.stderrData, response.stderrLength);
    exitCode = response.exitCode;

cleanup:
    if (pipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(pipe);
    }

    BufferFree(&requestPayload);
    BufferFree(&responsePayload);
    free(workingDirectory);
    free(pipeName);
    LocalFree(arguments);
    return exitCode;
}
