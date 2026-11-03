namespace ServiceLib.Manager;

/// <summary>One authorized session. Restarts only the immutable binary/config copied at Connect.
/// The owner may pause/resume this fixed session; there is no command execution channel.</summary>
public static class MacTunSupervisor
{
    private static string Q(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";
    public static string Script(int parentPid, string marker, string log, string installDns, string removeDns)
    {
        if (parentPid <= 0) throw new ArgumentOutOfRangeException(nameof(parentPid));
        return $$"""
        session=$1
        child=''
        active() { /bin/kill -0 {{parentPid}} 2>/dev/null && /bin/test -f {{Q(marker)}}; }
        requested() { [ "$(/bin/cat {{Q(marker)}} 2>/dev/null)" = active ]; }
        stop_child() {
          if [ -n "$child" ]; then
            /bin/kill -TERM "$child" 2>/dev/null || true
            tries=0
            while /bin/kill -0 "$child" 2>/dev/null && [ "$tries" -lt 20 ]; do /bin/sleep 0.1; tries=$((tries+1)); done
            /bin/kill -KILL "$child" 2>/dev/null || true
            wait "$child" 2>/dev/null || true
            child=''
          fi
          {{removeDns}}
        }
        cleanup() {
          stop_child
          /bin/rmdir {{Q(marker + ".idle")}} 2>/dev/null || true
          /bin/rm -rf "$session"
        }
        trap cleanup EXIT
        trap 'exit 0' TERM INT
        trap '' HUP
        delay=1
        while active; do
          if ! requested; then
            stop_child
            /bin/mkdir {{Q(marker + ".idle")}} 2>/dev/null || true
            while active && ! requested; do /bin/sleep 0.1; done
            /bin/rmdir {{Q(marker + ".idle")}} 2>/dev/null || true
            delay=1
            if ! active; then exit 0; fi
          fi
          "$session/core" run -c "$session/config.json" >> {{Q(log)}} 2>&1 < /dev/null & child=$!
          /usr/bin/printf 'World Science tunnel child pid=%s\n' "$child" >> {{Q(log)}}
          /bin/sleep 0.5
          if /bin/kill -0 "$child" 2>/dev/null; then
            if ! ( {{installDns}} ); then /bin/kill -TERM "$child" 2>/dev/null || true; fi
          fi
          ticks=0
          while active && requested && /bin/kill -0 "$child" 2>/dev/null; do /bin/sleep 0.25; ticks=$((ticks+1)); done
          if ! active; then exit 0; fi
          if ! requested; then continue; fi
          wait "$child" 2>/dev/null; result=$?; child=''
          {{removeDns}}
          /usr/bin/printf 'World Science tunnel exited (%s); retry in %ss\n' "$result" "$delay" >> {{Q(log)}}
          if [ "$ticks" -ge 120 ]; then delay=1; fi
          remaining=$delay
          while active && requested && [ "$remaining" -gt 0 ]; do /bin/sleep 1; remaining=$((remaining-1)); done
          delay=$((delay*2)); if [ "$delay" -gt 30 ]; then delay=30; fi
        done
        """;
    }
    public static string Launch(int parentPid, string binary, string config, string marker, string log, string installDns, string removeDns)
    {
        var script = Script(parentPid, marker, log, installDns, removeDns);
        return "umask 077; session=$(/usr/bin/mktemp -d /private/var/run/worldscience-tun.XXXXXXXX) || exit 1; " +
            $"if ! /bin/cp {Q(binary)} \"$session/core\" || ! /bin/cp {Q(config)} \"$session/config.json\"; then /bin/rm -rf \"$session\"; exit 1; fi; " +
            "/bin/chmod 500 \"$session/core\"; /bin/chmod 400 \"$session/config.json\"; trap '' HUP; " +
            $"/bin/sh -c {Q(script)} worldscience-session \"$session\" > /dev/null 2>&1 < /dev/null & echo $!";
    }
}
