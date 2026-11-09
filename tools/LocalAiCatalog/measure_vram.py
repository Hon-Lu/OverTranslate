# Re-measures the VRAM estimate of every model in src/OverTranslate/localai/models.json.
#
# Run from anywhere, after a new llama.cpp build or a change to the model list:
#   python measure_vram.py <llama.cpp folder with llama-fit-params.exe> <work folder on a roomy drive>
#
# Only the head of each GGUF is fetched. The file is then extended to its real size as a sparse
# file, which is all llama-fit-params reads, so a 14 GB model costs 64 MB of disk and of download.
# The launch parameters are the app's own (-c 4096 -ngl 99); -np 2 -kvu measures the same VRAM and
# llama-fit-params does not accept -kvu, so -np 1 stands in for them.
#
# Prints one line per model and the vramMiB the list should carry. It does not edit the list.
import json
import os
import re
import subprocess
import sys
import urllib.request

ARGS = ['-c', '4096', '-ngl', '99', '-np', '1']
LIST = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'OverTranslate', 'localai', 'models.json')


def sparse_copy(url, size, path, head):
    request = urllib.request.Request(url, headers={'User-Agent': 'measure', 'Range': f'bytes=0-{min(head, size) - 1}'})
    data = urllib.request.urlopen(request, timeout=120).read()
    with open(path, 'wb') as file:
        file.write(data)
    native = path.replace('/', '\\')
    subprocess.run(['fsutil', 'sparse', 'setflag', native], check=True, capture_output=True)
    subprocess.run(['fsutil', 'file', 'seteof', native, str(size)], check=True, capture_output=True)


def project(fit, path):
    run = subprocess.run([fit, '-m', path, *ARGS, '-lv', '4'], capture_output=True, text=True,
                         encoding='utf-8', errors='replace', timeout=300)
    text = run.stdout + run.stderr
    found = re.search(r'projected to use (\d+) MiB of device memory', text)
    return int(found.group(1)) if found else None, text[-800:]


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__ or 'measure_vram.py <llama.cpp folder> <work folder>')
    fit = os.path.join(sys.argv[1], 'llama-fit-params.exe')
    work = sys.argv[2]
    os.makedirs(work, exist_ok=True)

    with open(LIST, encoding='utf-8') as file:
        models = json.load(file)['models']

    for model in models:
        path = os.path.join(work, model['file'])
        if not os.path.exists(path) or os.path.getsize(path) != model['size']:
            sparse_copy(model['url'], model['size'], path, 64 << 20)
        vram, tail = project(fit, path)
        if vram is None:
            # A header that does not fit in the first 64 MB.
            sparse_copy(model['url'], model['size'], path, 256 << 20)
            vram, tail = project(fit, path)
        mark = '' if vram == model['vramMiB'] else '  <-- differs from the list'
        print(f"{model['id']:32s} {model['size'] / 1e9:6.2f} GB  list {model['vramMiB']:6d} MiB  measured {vram} MiB{mark}")
        if vram is None:
            print(tail)


if __name__ == '__main__':
    main()
