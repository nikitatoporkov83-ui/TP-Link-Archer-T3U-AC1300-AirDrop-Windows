"""Русские заголовки и сообщения стандартного командного интерфейса."""
import argparse
import sys


class ArgumentParser(argparse.ArgumentParser):
    def __init__(self, *args, **kwargs):
        kwargs.setdefault('add_help', False)
        super().__init__(*args, **kwargs)
        self._positionals.title = 'аргументы'
        self._optionals.title = 'параметры'
        self.add_argument('-h', '--help', action='help', help='показать справку и выйти')

    def format_usage(self):
        return super().format_usage().replace('usage: ', 'использование: ', 1)

    def format_help(self):
        return super().format_help().replace('usage: ', 'использование: ', 1)

    def error(self, message):
        for source, translated in {
            'the following arguments are required:': 'нужны обязательные параметры:',
            'unrecognized arguments:': 'неизвестные параметры:',
            'expected one argument': 'ожидается одно значение',
            'invalid choice:': 'недопустимое значение:',
            'invalid int value:': 'недопустимое целое число:',
            'argument ': 'параметр ',
            '(choose from ': '(выберите из ',
        }.items():
            message = message.replace(source, translated)
        self.print_usage(sys.stderr)
        self.exit(2, f'{self.prog}: ошибка: {message}\n')
