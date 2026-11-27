import subprocess


def get(url):
    result = subprocess.run(["curl.exe", "--fail", "--location", "--silent", "--show-error",
                             "--max-time", "30", "--retry", "1", url], capture_output=True)
    if result.returncode:
        raise OSError(f"Download request failed ({result.returncode}).")
    return result.stdout
