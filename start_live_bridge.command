#!/bin/zsh
cd -- "${0:A:h}"
/opt/homebrew/bin/python3 ./bridge.py "$@"
status_code=$?
if (( status_code != 0 )); then
  read -r "reply?按回车关闭窗口。"
fi
exit "$status_code"
