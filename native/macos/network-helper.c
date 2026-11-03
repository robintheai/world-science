// Fixed-function, per-user launchd helper. No paths, PIDs, configs or shell text arrive over IPC.
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <sys/select.h>
#include <libproc.h>
#include <signal.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static char root[PATH_MAX], runtime[PATH_MAX], sockpath[PATH_MAX];
static uid_t allowed_uid;
static pid_t child = 0, owner = 0;
static struct proc_bsdinfo owner_info;
static volatile sig_atomic_t quitting = 0;
static int active = 0, failures = 0;
static time_t retry_at = 0, started_at = 0;
static void on_signal(int sig) { (void)sig; quitting = 1; }
static void path(char *out, const char *base, const char *name) { snprintf(out, PATH_MAX, "%s/%s", base, name); }
static int info(pid_t pid, struct proc_bsdinfo *out) { return proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, out, sizeof(*out)) == sizeof(*out); }
static int same_process(pid_t pid, const struct proc_bsdinfo *before) {
    struct proc_bsdinfo now;
    return info(pid, &now) && now.pbi_uid == before->pbi_uid && now.pbi_start_tvsec == before->pbi_start_tvsec && now.pbi_start_tvusec == before->pbi_start_tvusec;
}
static void kill_child(pid_t pid) {
    if (pid <= 1) return;
    kill(pid, SIGTERM);
    for (int i = 0; i < 20; i++) {
        if (waitpid(pid, NULL, WNOHANG) == pid || (kill(pid, 0) < 0 && errno == ESRCH)) return;
        usleep(100000);
    }
    kill(pid, SIGKILL); waitpid(pid, NULL, 0);
}
static int dns(const char *name) {
    char file[PATH_MAX]; path(file, root, name);
    pid_t p = fork();
    if (p == 0) { execl("/bin/sh", "sh", file, (char *)NULL); _exit(127); }
    if (p < 0) return 0;
    int status = 0;
    for (int i = 0; i < 50; i++) {
        if (waitpid(p, &status, WNOHANG) == p) return WIFEXITED(status) && WEXITSTATUS(status) == 0;
        usleep(100000);
    }
    kill_child(p); return 0;
}
static void remember_child(void) {
    char file[PATH_MAX]; path(file, runtime, "child");
    struct proc_bsdinfo b;
    if (!info(child, &b)) return;
    int fd = open(file, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW, 0600);
    if (fd >= 0) { dprintf(fd, "%d %u %llu %llu\n", child, b.pbi_uid, b.pbi_start_tvsec, b.pbi_start_tvusec); close(fd); }
}
static void stop(void) {
    if (child) { kill_child(child); child = 0; }
    dns("dns-off.sh");
    char file[PATH_MAX]; path(file, runtime, "child"); unlink(file);
}
static void recover_orphan(void) {
    char file[PATH_MAX]; path(file, runtime, "child");
    int fd = open(file, O_RDONLY | O_NOFOLLOW);
    if (fd >= 0) {
        FILE *f = fdopen(fd, "r"); struct proc_bsdinfo b = {0}; int pid;
        if (f && fscanf(f, "%d %u %llu %llu", &pid, &b.pbi_uid, &b.pbi_start_tvsec, &b.pbi_start_tvusec) == 4 && pid > 1 && b.pbi_uid == geteuid() && same_process(pid, &b)) kill_child(pid);
        if (f) fclose(f); else close(fd);
    }
    stop();
}
static int start(void) {
    char core[PATH_MAX], config[PATH_MAX]; path(core, root, "core"); path(config, root, "config.json");
    child = fork();
    if (child == 0) {
        // Root runs only the immutable installed core, with a clean, fixed environment.
        chdir(root);
        char *const args[] = { core, "run", "-c", config, NULL };
        char *const env[] = { "PATH=/usr/bin:/bin:/usr/sbin:/sbin", "HOME=/var/root", NULL };
        execve(core, args, env); _exit(127);
    }
    if (child < 0) { child = 0; return 0; }
    remember_child(); usleep(400000);
    if (waitpid(child, NULL, WNOHANG) == child) { child = 0; stop(); return 0; }
    if (!dns("dns-on.sh")) { stop(); return 0; }
    started_at = time(NULL); return 1;
}
static void reply(int fd, const char *text) { send(fd, text, strlen(text), 0); }
static void request(int fd) {
    uid_t uid; gid_t gid; pid_t peer = 0; socklen_t len = sizeof(peer);
    if (getpeereid(fd, &uid, &gid) || uid != allowed_uid || getsockopt(fd, SOL_LOCAL, LOCAL_PEERPID, &peer, &len) || peer <= 1) { reply(fd, "DENIED\n"); return; }
    struct timeval timeout = {1, 0}; setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)); setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
    char command[16] = {0}; size_t used = 0;
    struct timespec began, now; clock_gettime(CLOCK_MONOTONIC, &began);
    while (used < sizeof(command)-1) {
        clock_gettime(CLOCK_MONOTONIC, &now);
        if ((now.tv_sec - began.tv_sec) * 1000000000LL + now.tv_nsec - began.tv_nsec > 1000000000LL) { reply(fd, "INVALID\n"); return; }
        ssize_t n = recv(fd, command + used, 1, 0);
        if (n != 1) { reply(fd, "INVALID\n"); return; }
        if (command[used++] == '\n') break;
    }
    if (!strcmp(command, "STATUS\n")) { char out[64]; snprintf(out, sizeof(out), "OK 1 %d %s\n", getpid(), active ? "ACTIVE" : "IDLE"); reply(fd, out); return; }
    if (strcmp(command, "START\n") && strcmp(command, "STOP\n")) { reply(fd, "INVALID\n"); return; }
    if (owner && owner != peer && same_process(owner, &owner_info)) { reply(fd, "BUSY\n"); return; }
    if (!strcmp(command, "STOP\n")) { active = 0; stop(); owner = 0; failures = 0; reply(fd, "OK\n"); return; }
    struct proc_bsdinfo b;
    if (!info(peer, &b) || b.pbi_uid != allowed_uid) { reply(fd, "DENIED\n"); return; }
    owner = peer; owner_info = b; active = 1; failures = 0;
    if (!child && !start()) { active = 0; owner = 0; reply(fd, "FAILED\n"); return; }
    reply(fd, "OK\n");
}
int main(int argc, char **argv) {
    umask(077); signal(SIGTERM, on_signal); signal(SIGINT, on_signal); signal(SIGHUP, SIG_IGN); signal(SIGPIPE, SIG_IGN);
    if (argc == 5 && !strcmp(argv[1], "--test") && geteuid() != 0) {
        // An unprivileged fixture cannot be used as a production/root path override.
        snprintf(root, sizeof(root), "%s", argv[2]); snprintf(runtime, sizeof(runtime), "%s", argv[3]); allowed_uid = getuid();
        if (strcmp(argv[4], "isolated")) return 2;
    } else {
        if (argc != 2 || geteuid() != 0) return 2;
        char *end; unsigned long value = strtoul(argv[1], &end, 10);
        if (*end || value < 501 || value > INT_MAX) return 2;
        allowed_uid = (uid_t)value;
        snprintf(root, sizeof(root), "/Library/PrivilegedHelperTools/io.github.robintheai.worldscience.network-%u", allowed_uid);
        snprintf(runtime, sizeof(runtime), "/private/var/run/io.github.robintheai.worldscience.network-%u", allowed_uid);
    }
    if (mkdir(runtime, 0755) && errno != EEXIST) return 3;
    struct stat st;
    if (lstat(runtime, &st) || !S_ISDIR(st.st_mode) || st.st_uid != geteuid() || (st.st_mode & 0022)) return 3;
    chmod(runtime, 0755);
    path(sockpath, runtime, "control.sock");
    // launchd guarantees one production instance. An advisory lock protects fixture/manual launches too.
    char lockpath[PATH_MAX]; path(lockpath, runtime, "lock");
    int lock = open(lockpath, O_CREAT | O_RDWR | O_NOFOLLOW, 0600);
    if (lock < 0 || flock(lock, LOCK_EX | LOCK_NB)) return 4;
    fcntl(lock, F_SETFD, FD_CLOEXEC);
    recover_orphan();
    int server = socket(AF_UNIX, SOCK_STREAM, 0); if (server < 0) return 5;
    fcntl(server, F_SETFD, FD_CLOEXEC);
    struct sockaddr_un address = {0}; address.sun_family = AF_UNIX;
    if (strlen(sockpath) >= sizeof(address.sun_path)) return 6;
    strcpy(address.sun_path, sockpath); unlink(sockpath);
    if (bind(server, (struct sockaddr *)&address, sizeof(address)) || chmod(sockpath, 0600) || chown(sockpath, allowed_uid, -1) || listen(server, 8)) return 7;
    while (!quitting) {
        if (active && !same_process(owner, &owner_info)) { active = 0; stop(); owner = 0; }
        if (child && waitpid(child, NULL, WNOHANG) == child) {
            child = 0; stop();
            if (time(NULL) - started_at > 30) failures = 0;
            int delay = 1 << (failures < 5 ? failures++ : 5); retry_at = time(NULL) + delay;
        }
        if (active && !child && time(NULL) >= retry_at && !start()) retry_at = time(NULL) + 5;
        fd_set fds; FD_ZERO(&fds); FD_SET(server, &fds); struct timeval wait = {0, 200000};
        if (select(server+1, &fds, NULL, NULL, &wait) > 0) { int fd = accept(server, NULL, NULL); if (fd >= 0) { fcntl(fd, F_SETFD, FD_CLOEXEC); request(fd); close(fd); } }
    }
    active = 0; stop(); close(server); unlink(sockpath); close(lock); return 0;
}
