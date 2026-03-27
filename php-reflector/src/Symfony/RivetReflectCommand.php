<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Symfony;

use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\Diagnostics;
use Rivet\PhpReflector\SymfonyRouteWalker;
use Symfony\Component\Console\Attribute\AsCommand;
use Symfony\Component\Console\Command\Command;
use Symfony\Component\Console\Input\InputInterface;
use Symfony\Component\Console\Input\InputOption;
use Symfony\Component\Console\Output\OutputInterface;
use Symfony\Component\Routing\RouterInterface;

#[AsCommand(name: 'rivet:reflect', description: 'Generate Rivet contract JSON from Symfony routes')]
class RivetReflectCommand extends Command
{
    public function __construct(private readonly RouterInterface $router)
    {
        parent::__construct();
    }

    protected function configure(): void
    {
        $this->addOption('out', null, InputOption::VALUE_REQUIRED, 'Output file path');
    }

    protected function execute(InputInterface $input, OutputInterface $output): int
    {
        $routes = SymfonyRouteWalker::fromRouteCollection($this->router->getRouteCollection());
        $contract = SymfonyRouteWalker::walk($routes);

        /** @var Diagnostics $diagnostics */
        $diagnostics = $contract['diagnostics'];
        foreach ($diagnostics->formatMessages() as $line) {
            $output->writeln("<comment>$line</comment>");
        }

        if ($diagnostics->hasErrors()) {
            return Command::FAILURE;
        }

        $json = ContractEmitter::emit($contract);

        $out = $input->getOption('out');
        if ($out !== null) {
            file_put_contents($out, $json);
            $output->writeln("<info>Contract written to $out</info>");
        } else {
            $output->writeln($json);
        }

        return Command::SUCCESS;
    }
}
