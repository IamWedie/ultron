import re
txt = open(r'C:\Users\wadia\ultron_winui\backend\telegram_client.py',
           encoding='utf-8').read()

types = sorted(set(re.findall(r'"telegram_[a-z_]+"', txt)))
print('IPC event/handler types:', types)

print('--- dispatch (msg_type comparisons) ---')
for i, ln in enumerate(txt.splitlines(), 1):
    ls = ln.strip()
    if 'msg_type' in ls and '==' in ls:
        print(i, '|', ls)

print('--- seams: async def + where telegram_target handled ---')
for i, ln in enumerate(txt.splitlines(), 1):
    ls = ln.strip()
    if ls.startswith('async def '):
        print(i, '|', ls)
