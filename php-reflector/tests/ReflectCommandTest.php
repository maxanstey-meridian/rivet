<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ReflectCommand;

class ReflectCommandTest extends TestCase
{
    public function testRunProducesJsonFile(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/Fixtures', $tmpFile);

            $this->assertSame(0, $exitCode);
            $this->assertFileExists($tmpFile);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $this->assertArrayHasKey('types', $decoded);
            $this->assertArrayHasKey('enums', $decoded);
            $this->assertArrayHasKey('endpoints', $decoded);

            $typeNames = array_column($decoded['types'], 'name');
            $this->assertContains('ScalarDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testRunWithMissingDirReturnsError(): void
    {
        $command = new ReflectCommand();
        $this->assertSame(1, $command->run('/nonexistent', '/dev/null'));
    }
}
