<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;

class ComposerPackageTest extends TestCase
{
    private static array $composer;
    private static string $packageRoot;

    public static function setUpBeforeClass(): void
    {
        self::$packageRoot = dirname(__DIR__);
        $json = file_get_contents(self::$packageRoot . '/composer.json');
        self::$composer = json_decode($json, true, 512, JSON_THROW_ON_ERROR);
    }

    public function testComposerJsonIsValid(): void
    {
        $this->assertArrayHasKey('name', self::$composer);
        $this->assertArrayHasKey('description', self::$composer);
        $this->assertArrayHasKey('license', self::$composer);
        $this->assertArrayHasKey('autoload', self::$composer);
        $this->assertArrayHasKey('require', self::$composer);

        $this->assertSame('MIT', self::$composer['license']);
        $this->assertSame('library', self::$composer['type']);

        $this->assertArrayHasKey('keywords', self::$composer);
        $this->assertNotEmpty(self::$composer['keywords']);
    }

    public function testLicenseFileExists(): void
    {
        $licensePath = self::$packageRoot . '/LICENSE';
        $this->assertFileExists($licensePath);
        $this->assertStringContainsString('MIT License', file_get_contents($licensePath));
    }

    public function testPhpunitConfigExists(): void
    {
        $path = self::$packageRoot . '/phpunit.xml.dist';
        $this->assertFileExists($path);

        $xml = simplexml_load_file($path);
        $this->assertNotFalse($xml, 'phpunit.xml.dist is not valid XML');

        $dirs = $xml->xpath('//testsuite/directory');
        $this->assertNotEmpty($dirs);
        $this->assertSame('tests', (string) $dirs[0]);
    }

    public function testBinEntryPointDeclared(): void
    {
        $this->assertArrayHasKey('bin', self::$composer);
        $this->assertContains('bin/rivet-reflect', self::$composer['bin']);

        $binPath = self::$packageRoot . '/bin/rivet-reflect';
        $this->assertFileExists($binPath);
        $this->assertTrue(is_executable($binPath), 'bin/rivet-reflect is not executable');
    }

    public function testGitignoreExcludesVendor(): void
    {
        $gitignore = file_get_contents(self::$packageRoot . '/.gitignore');
        $this->assertStringContainsString('/vendor/', $gitignore);
    }

    public function testReadmeExistsWithRequiredSections(): void
    {
        $readmePath = self::$packageRoot . '/README.md';
        $this->assertFileExists($readmePath);

        $content = file_get_contents($readmePath);
        $requiredSections = ['Install', 'Quick start', 'DTO conventions', 'Attributes', 'CLI', 'Build integration'];

        foreach ($requiredSections as $section) {
            $this->assertMatchesRegularExpression(
                '/^##\s+.*' . preg_quote($section, '/') . '/mi',
                $content,
                "README is missing a heading containing '$section'",
            );
        }
    }
}
