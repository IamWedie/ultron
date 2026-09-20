import re

txt = open(r'C:\Users\wadia\ultron_winui\backend\telegram_client.py',
           encoding='utf-8').read()
pat = re.compile(r'"(telegram_[a-z_]+)"')
seen = []
for m in pat.finditer(txt):
    if m.group(1) not in seen:
        seen.append(m.group(1))
print("IPC types:", seen)

seam = re.compile(r'(msg_type|cmd|command)\s*==\s*"(telegram_\w+)"')
print("--- dispatch seams ---")
for m in seam.finditer(txt):
    i = txt[:m.start()].count("\n") + 1
    print(i, '|', m.group( rails0), m.group(1))
