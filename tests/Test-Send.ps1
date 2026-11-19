param([string]$Exe)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '../build/HLK-SerialDesk.exe' }
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run with Windows PowerShell -STA.'
}
Add-Type -AssemblyName System.Windows.Forms
# Proxy the EXE's internal transport interface directly. The live worker runs,
# but Open, Write and presence checks cannot reach Windows serial devices.
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Linq.Expressions;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using System.Threading;

public sealed class SendTestTransport : RealProxy
{
    private readonly Type contract;
    private readonly ManualResetEventSlim blocked = new ManualResetEventSlim(false);
    private readonly ManualResetEventSlim release = new ManualResetEventSlim(true);
    private int gate;
    private int writes;
    private volatile string last;
    private byte[] receive;
    public volatile bool FailWrite;
    public int Attempts { get { return Volatile.Read(ref writes); } }
    public string LastWrite { get { return last; } }
    public bool ReadBlocked { get { return blocked.IsSet; } }

    public SendTestTransport(Type type) : base(type) { contract = type; }

    public Delegate Factory()
    {
        return Expression.Lambda(typeof(Func<>).MakeGenericType(contract),
            Expression.Constant(GetTransparentProxy(), contract)).Compile();
    }

    public void HoldRead()
    {
        blocked.Reset();
        release.Reset();
        Interlocked.Exchange(ref gate, 1);
    }

    public void Unblock() { release.Set(); }
    public void Feed(string value) { Volatile.Write(ref receive, Encoding.ASCII.GetBytes(value)); }

    public override IMessage Invoke(IMessage message)
    {
        var call = (IMethodCallMessage)message;
        try
        {
            object reply;
            switch (call.MethodName)
            {
                case "GetType": reply = contract; break;
                case "Open": case "Dispose": reply = null; break;
                case "get_IsPresent": reply = true; break;
                case "get_BytesToRead":
                    if (Interlocked.Exchange(ref gate, 0) != 0)
                    {
                        blocked.Set();
                        if (!release.Wait(8000)) throw new TimeoutException("Test did not release the fake read.");
                    }
                    byte[] input = Volatile.Read(ref receive);
                    reply = input == null ? 0 : input.Length;
                    break;
                case "Read":
                    byte[] data = Interlocked.Exchange(ref receive, null);
                    int n = data == null ? 0 : Math.Min(data.Length, (int)call.Args[2]);
                    if (n != 0) Buffer.BlockCopy(data, 0, (byte[])call.Args[0], (int)call.Args[1], n);
                    reply = n;
                    break;
                case "Write":
                    Interlocked.Increment(ref writes);
                    if (FailWrite) throw new IOException("simulated serial write failure");
                    last = Encoding.UTF8.GetString((byte[])call.Args[0], (int)call.Args[1], (int)call.Args[2]);
                    reply = null;
                    break;
                default: throw new NotSupportedException(call.MethodName);
            }
            return new ReturnMessage(reply, null, 0, call.LogicalCallContext, call);
        }
        catch (Exception ex) { return new ReturnMessage(ex, call); }
    }
}
"@

[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Exe).Path)
$app = [Activator]::CreateInstance($assembly.GetType('SerialDesk'), [object[]]@($null))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$allFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
function Field([string]$Name) { return ,($app.GetType().GetField($Name, $flags).GetValue($app)) }
function Set-Field([string]$Name, $Value) { $app.GetType().GetField($Name, $flags).SetValue($app, $Value) }
function Call([string]$Name) { $app.GetType().GetMethod($Name, $flags).Invoke($app, $null) | Out-Null }
function Assert([bool]$Check, [string]$Reason) { if (-not $Check) { throw $Reason } }
function Wait-UI([scriptblock]$Condition, [string]$Reason) {
    $deadline = [DateTime]::UtcNow.AddSeconds(6)
    do {
        [Windows.Forms.Application]::DoEvents()
        if (& $Condition) { return }
        if ([DateTime]::UtcNow -gt $deadline) { throw $Reason }
        Start-Sleep -Milliseconds 15
    } while ($true)
}
function Queue-Draft([string]$Draft) {
    $device.HoldRead()
    Wait-UI { $device.ReadBlocked } 'Fake read gate did not engage.'
    $entry.Text = $Draft
    Assert $send.Enabled 'Send should be available with TX enabled.'
    $send.PerformClick()
    Assert ((Field 'sendPending') -and $entry.Text -eq $Draft -and $entry.Enabled -and -not $send.Enabled) 'Pending TX should keep the draft and disable repeat send.'
    Assert ((Field 'txFeedback') -match '发送|写入') 'Pending TX should have visible feedback.'
}
$base = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testFolder = [IO.Path]::Combine($base, 'HLK-Send-Test-' + [Guid]::NewGuid().ToString('N'))
$device = $null
try {
    $app.Show()
    Wait-UI { -not (Field 'refreshing') } 'Port enumeration did not finish.'
    $entry = Field 'entry'
    $send = Field 'send'
    $tx = Field 'tx'
    $contract = $assembly.GetType('ISerialTransport', $true)
    $device = [SendTestTransport]::new($contract)
    $constructor = @($assembly.GetType('LiveSerial').GetConstructors($allFlags) |
        Where-Object { $_.GetParameters().Length -eq 5 })
    Assert ($constructor.Count -eq 1) 'Cannot locate the transport injection constructor.'
    $worker = $constructor[0].Invoke([object[]]@($testFolder, 'TEST-ONLY', 57600, (Field 'inbox'), $device.Factory()))
    Set-Field 'live' $worker
    Set-Field 'opening' $true
    $worker.Start()
    Wait-UI { -not (Field 'opening') -and $tx.Enabled } 'Fake serial session did not open.'
    Assert (-not $send.Enabled -and -not $tx.Checked) 'Connected session must start RX-only.'
    $tx.Checked = $true

    Queue-Draft ('offline-first' + [char]13 + [char]10)
    $device.Unblock()
    Wait-UI { -not (Field 'sendPending') } 'Missing TX acknowledgment.'
    Assert ($device.Attempts -eq 1 -and $device.LastWrite -ceq "offline-first" + [char]13) 'Fake write mismatch (including CR suffix).'
    Assert ($entry.Text -eq '' -and (Field 'txBytes') -eq 14 -and (Field 'txFeedback') -match '已发|已写') 'Successful TX should clear the draft and confirm the byte count.'

    Queue-Draft 'offline-old'
    $entry.Text = 'offline-new'
    $device.Unblock()
    Wait-UI { -not (Field 'sendPending') } 'Missing acknowledgment for the old draft.'
    Assert ($device.Attempts -eq 2 -and $entry.Text -ceq 'offline-new') 'An old acknowledgment must not clear an edited draft.'

    Queue-Draft 'offline-cancel'
    $tx.Checked = $false
    $device.Unblock()
    Wait-UI { -not (Field 'sendPending') -and (Field 'txFeedback') -match '取消|未发送' } 'Missing cancel feedback.'
    Assert ($device.Attempts -eq 2 -and $entry.Text -ceq 'offline-cancel') 'Canceled TX should preserve the draft without writing.'

    $tx.Checked = $true
    Queue-Draft 'offline-bootlock'
    $device.Feed("Please choose the operation:" + [char]10)
    $device.Unblock()
    Wait-UI { (Field 'markers').InBootloader -and -not (Field 'sendPending') } 'U-Boot did not lock send.'
    Assert (-not $tx.Enabled -and -not $send.Enabled -and $device.Attempts -eq 2 -and $entry.Text -ceq 'offline-bootlock') 'U-Boot must prevent writes, including queued writes.'
    $device.Feed("procd: - init complete -" + [char]10)
    Wait-UI { $tx.Enabled } 'TX control did not return on init completion.'
    Assert (-not $tx.Checked -and -not $send.Enabled) 'Leaving U-Boot must not automatically re-enable TX.'

    $tx.Checked = $true
    Queue-Draft 'offline-failure'
    $device.FailWrite = $true
    $device.Unblock()
    Wait-UI { $null -eq (Field 'live') } 'Failed write did not end the session.'
    Assert ($device.Attempts -eq 3 -and $entry.Text -ceq 'offline-failure' -and -not $send.Enabled) 'Failed TX must preserve draft and disable send.'
    Assert ((Field 'txFeedback') -match '失败|未确认|部分') 'Failed TX should show an explicit result.'
    Assert ((Field 'txBytes') -eq 26) 'Only confirmed bytes should increment TX count.'
    $folder = Field 'currentFolder'
    Assert (Test-Path -LiteralPath (Join-Path $folder 'SHA256SUMS.txt')) 'Simulated capture was not sealed.'
    'PASS: built EXE, TX acknowledgment, edited draft, cancellation, bootloader lock, write failure, capture sealed. No physical port opened.'
}
finally {
    if ($null -ne $device) { $device.Unblock() }
    try {
        $worker = Field 'live'
        if ($null -ne $worker) {
            $worker.Stop()
            Wait-UI { $null -eq (Field 'live') } 'Fake session did not stop.'
        }
        $app.Close()
    }
    finally {
        $app.Dispose()
        $resolved = [IO.Path]::GetFullPath($testFolder)
        if ($resolved.StartsWith($base + '\HLK-Send-Test-', [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolved)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
